using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AcNotes.Windows
{
    /// <summary>单条笔记（对应 macOS NoteTab）。class：List 索引可直接改属性</summary>
    public sealed class NoteTab
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int? SelectionStart { get; set; }
        public int? SelectionLength { get; set; }

        public static NoteTab New(string text = "") => new()
        {
            Id = Guid.NewGuid(),
            Text = text,
            CreatedAt = DateTime.Now,
            SelectionStart = 0,
            SelectionLength = 0,
        };
    }

    /// <summary>
    /// 多标签笔记存储。持久化设计（2026-08-10 重构，目标：用户输入的每一个字实时落盘，关机/重启/断电不丢）：
    /// - 实时写：无防抖延迟，每次内容变更立即入队后台写盘（写线程串行消费 + 合并积压，只写最新快照）
    /// - 落盘即 fsync：每次写都 FlushFileBuffers 刷到介质（断电不丢已确认写入）
    /// - 三副本：notes.json 主存档（tmp+Move 原子写）+ notes.json.bak 上次成功快照 + 注册表兼容副本
    /// - 失败重试：写失败快照保留待重试，禁止静默丢失
    /// - 关机路径：内存是唯一事实源（推路径实时同步），Flush 同步写内存快照，不依赖编辑器异步拉取
    /// 加载时按 SavedAt 取较新快照，主文件损坏时逐级回退 .bak → .tmp → 注册表。
    /// </summary>
    public sealed class NoteStore
    {
        private const string RegistryRoot = @"Software\AcNotes";
        private const string RegistryJsonKey = "NotesJsonV1";

        private sealed class PersistedNotes
        {
            public List<NoteTab> Tabs { get; set; } = new();
            public Guid ActiveTabId { get; set; }
            public DateTime SavedAt { get; set; }
        }

        private sealed class DeletedNote
        {
            public NoteTab Tab { get; init; } = default;
            public int Index { get; init; }
        }

        private readonly string _archivePath;
        private readonly bool _useRegistry;   // 2026-08-11：selftest 临时 store 置 false，完全隔离用户注册表副本
        private readonly object _lock = new();        // 内存数据 + 写队列状态
        private readonly object _writeLock = new();   // 磁盘写串行化（写线程 / Flush 互斥）
        private PersistedNotes? _pendingWrite;        // 最新待写快照（合并写：只保留最新）
        private bool _writeRunning;                   // 写线程是否活跃
        private DeletedNote? _recentlyDeleted;
        private DateTime _lastModifiedAt = DateTime.MinValue;  // 内存最后修改时刻（2026-08-14，周期兜底判脏）
        private DateTime _lastPersistedAt = DateTime.MinValue; // 磁盘最后成功落盘时刻（2026-08-14）

        /// <summary>
        /// 磁盘写盘失败通知（2026-08-14：写盘失败静默无告警，用户无感知持续输入 → 关机全丢。
        /// UI 侧弹托盘气泡提醒；事件可在任意线程触发，订阅方负责跨线程调度与节流）
        /// </summary>
        public event Action<string>? PersistFailed;

        public List<NoteTab> Tabs { get; private set; } = new();
        public Guid ActiveTabId { get; private set; }

        public NoteStore() : this(null, true) { }

        /// <summary>
        /// archivePath 可注入（selftest 用独立临时路径，不污染用户数据，2026-08-11）。
        /// useRegistry=false 时读写均绕过注册表副本（selftest 隔离，防临时数据污染用户注册表键）。
        /// </summary>
        public NoteStore(string? archivePath, bool useRegistry = true)
        {
            _archivePath = archivePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcNotes", "notes.json");
            _useRegistry = useRegistry;

            var snapshot = LoadLatestSnapshot();
            var storedTabs = snapshot?.Tabs ?? new List<NoteTab>();
            if (storedTabs.Count == 0)
            {
                storedTabs.Add(NoteTab.New());
            }

            Tabs = storedTabs;
            var preferred = snapshot?.ActiveTabId ?? Guid.Empty;
            ActiveTabId = preferred != Guid.Empty && Tabs.Any(t => t.Id == preferred)
                ? preferred
                : Tabs[0].Id;

            // 首次落盘：同步写内存快照（原子写 + fsync），保证双通道同步
            PersistToDisk(TakeSnapshot());
        }

        public string Text => Tabs[ActiveIndex].Text;

        public int ActiveIndex => Tabs.FindIndex(t => t.Id == ActiveTabId) is var i && i >= 0 ? i : 0;

        public void UpdateText(string nextText)
        {
            lock (_lock)
            {
                if (Tabs[ActiveIndex].Text == nextText) return;
                // 空值保护（2026-08-11 排查"退出后选中 tab 内容丢失"）：
                // GetHtmlAsync 在编辑器未就绪/异常时返回 ""（Tiptap 空文档 getHTML 返回 <p></p> 而非 ""），
                // 退出路径 SaveEditorStateAsync 拿 "" 调 UpdateText 会把已有内容清空 → Flush 落盘空 → 重启丢失。
                // 空串一律不覆盖非空内容；用户真清空笔记时 Tiptap 给的是 <p></p> 不是 ""。
                if (string.IsNullOrEmpty(nextText) && !string.IsNullOrEmpty(Tabs[ActiveIndex].Text))
                {
                    Console.WriteLine("[NoteStore] UpdateText rejected empty (would wipe existing content)");
                    return;
                }
                Tabs[ActiveIndex].Text = nextText;
                ClampSelectionFor(ActiveTabId);
            }
            ScheduleSave();
        }

        /// <summary>
        /// 按 tab id 写入内容（2026-08-11 新增，解决标签切换竞态错写）：
        /// 编辑器 onUpdate 上报的 payload 携带来源 tab id，按 id 定位写入，避免切 tab 瞬间
        /// 旧 tab 的延迟 payload 被写进新 tab（原 UpdateText 固定写 ActiveTabId）。
        /// 空值保护同 UpdateText。
        /// </summary>
        public void UpdateTextFor(Guid id, string nextText)
        {
            bool changed = false;
            lock (_lock)
            {
                var index = Tabs.FindIndex(t => t.Id == id);
                if (index < 0) return;
                if (Tabs[index].Text == nextText) return;
                if (string.IsNullOrEmpty(nextText) && !string.IsNullOrEmpty(Tabs[index].Text))
                {
                    Console.WriteLine("[NoteStore] UpdateTextFor rejected empty (would wipe existing content)");
                    return;
                }
                Tabs[index].Text = nextText;
                ClampSelectionFor(id);
                changed = true;
            }
            if (changed) ScheduleSave();
        }

        public void AddTab()
        {
            lock (_lock)
            {
                var tab = NoteTab.New();
                Tabs.Add(tab);
                ActiveTabId = tab.Id;
            }
            ScheduleSave();
        }

        public void RemoveActiveTab() => RemoveTab(ActiveTabId);

        public void RemoveTab(Guid id)
        {
            bool removed = false;
            lock (_lock)
            {
                if (Tabs.Count <= 1) return;
                var index = Tabs.FindIndex(t => t.Id == id);
                if (index < 0) return;

                _recentlyDeleted = new DeletedNote { Tab = Tabs[index], Index = index };
                Tabs.RemoveAt(index);
                if (id == ActiveTabId)
                {
                    var next = Math.Min(index, Tabs.Count - 1);
                    ActiveTabId = Tabs[next].Id;
                }
                removed = true;
            }
            if (removed) ScheduleSave();
        }

        public bool CanRestoreDeletedNote => _recentlyDeleted != null;

        public void RestoreLastDeletedTab()
        {
            bool restored = false;
            lock (_lock)
            {
                if (_recentlyDeleted == null) return;
                var d = _recentlyDeleted;
                var insertIndex = Math.Clamp(d.Index, 0, Tabs.Count);
                Tabs.Insert(insertIndex, d.Tab);
                ActiveTabId = d.Tab.Id;
                _recentlyDeleted = null;
                restored = true;
            }
            if (restored) ScheduleSave();
        }

        public void SelectTab(Guid id)
        {
            bool changed = false;
            lock (_lock)
            {
                if (!Tabs.Any(t => t.Id == id) || id == ActiveTabId) return;
                ActiveTabId = id;
                changed = true;
            }
            if (changed) ScheduleSave();
        }

        public void UpdateSelection(Guid id, int start, int length)
        {
            bool changed = false;
            lock (_lock)
            {
                var index = Tabs.FindIndex(t => t.Id == id);
                if (index < 0) return;
                var (s, l) = ClampedRange(start, length, Tabs[index].Text);
                if (Tabs[index].SelectionStart == s && Tabs[index].SelectionLength == l) return;
                Tabs[index].SelectionStart = s;
                Tabs[index].SelectionLength = l;
                changed = true;
            }
            if (changed) ScheduleSave();
        }

        public (int Start, int Length) SelectionRange(Guid id)
        {
            var tab = Tabs.FirstOrDefault(t => t.Id == id);
            if (tab.Id == Guid.Empty) return (0, 0);
            return ClampedRange(tab.SelectionStart ?? 0, tab.SelectionLength ?? 0, tab.Text);
        }

        /// <summary>标签显示名：有笔记内容取纯文本前 5 字符（最多），空笔记显示"未命名+序号"（用户 2026-08-03 需求）</summary>
        public string Title(Guid id)
        {
            var index = Tabs.FindIndex(t => t.Id == id);
            if (index < 0) return "未命名";

            var text = StripHtml(Tabs[index].Text).Trim();
            if (string.IsNullOrEmpty(text)) return $"未命名{index + 1}";
            return text.Length > 5 ? text[..5] : text;
        }

        /// <summary>完整标题（首行剥离 HTML → 42 字符截断），用于 ToolTip 等 hover 提示</summary>
        public string FullTitle(Guid id)
        {
            var index = Tabs.FindIndex(t => t.Id == id);
            if (index < 0) return "Untitled";

            var title = FirstMeaningfulLine(StripHtml(Tabs[index].Text)) ?? "";
            title = title.Trim();
            if (string.IsNullOrEmpty(title)) return $"Untitled {index + 1}";
            return title.Length > 42 ? title[..41] + "…" : title;
        }

        /// <summary>剥离 HTML 标签与实体（存储格式为 Tiptap HTML）</summary>
        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, "&nbsp;", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, "&amp;", "&");
            text = System.Text.RegularExpressions.Regex.Replace(text, "&lt;", "<");
            text = System.Text.RegularExpressions.Regex.Replace(text, "&gt;", ">");
            return text;
        }

        /// <summary>
        /// 同步落盘：拍最新内存快照直接写盘（不走写队列），用于收起面板 / 关闭 / 关机路径。
        /// 关闭/关机路径必须同步等待 fsync 完成再返回，保证数据落介质。
        /// </summary>
        public void Flush(bool flushToDisk = true)
        {
            PersistedNotes snapshot;
            lock (_lock)
            {
                snapshot = TakeSnapshot();
                _pendingWrite = null; // 已同步写最新快照，作废写队列中的旧快照
            }
            PersistToDisk(snapshot, flushToDisk);
        }

        // ---- 持久化 ----

        /// <summary>
        /// 实时写盘调度：每次内容变更立即入队（无防抖延迟），写线程串行消费并合并积压。
        /// 慢速输入 = 每变更一写（实时）；快速输入 = 写线程持续写最新快照，磁盘永远追赶内存。
        /// </summary>
        private void ScheduleSave()
        {
            lock (_lock)
            {
                _lastModifiedAt = DateTime.Now; // 所有修改路径统一经此标记（2026-08-14）
                _pendingWrite = TakeSnapshot(); // 覆盖式合并：只保留最新快照
                if (_writeRunning) return;      // 写线程活跃：写完会再取 pending
                _writeRunning = true;
            }
            ThreadPool.QueueUserWorkItem(WriteLoop);
        }

        /// <summary>
        /// 周期兜底落盘（2026-08-14）：仅在内存与磁盘不一致时写（防空转刷盘）。
        /// 防 WriteLoop 静默挂死/被系统抑制后，内存与磁盘差距持续累积。
        /// </summary>
        public void FlushIfDirty()
        {
            lock (_lock)
            {
                if (_pendingWrite == null && _lastPersistedAt >= _lastModifiedAt) return;
            }
            Flush(true);
        }

        /// <summary>后台写线程：串行消费 pending 快照，写失败保留待重试（禁止静默丢失）</summary>
        private void WriteLoop(object? _)
        {
            while (true)
            {
                PersistedNotes? snapshot;
                lock (_lock)
                {
                    snapshot = _pendingWrite;
                    _pendingWrite = null;
                    if (snapshot == null) { _writeRunning = false; return; } // 队列空，退出
                }

                if (PersistToDisk(snapshot)) continue;

                // 写失败：快照保留待重试（若期间已有更新的快照入队则让位），退出写循环
                lock (_lock)
                {
                    if (_pendingWrite == null) _pendingWrite = snapshot;
                    _writeRunning = false;
                }
                return;
            }
        }

        /// <summary>持久化单次快照：主存档（原子写 + fsync）+ .bak 轮换 + 注册表副本。返回是否成功。</summary>
        private bool PersistToDisk(PersistedNotes snapshot, bool flushToDisk = true)
        {
            try
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOpts);
                lock (_writeLock)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(_archivePath)!;
                        Directory.CreateDirectory(dir);
                        // .bak 轮换：当前主文件 → 上次成功快照备份（防主文件被写坏/半写）
                        try
                        {
                            if (File.Exists(_archivePath))
                                File.Copy(_archivePath, _archivePath + ".bak", overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[NoteStore] bak copy failed: {ex.Message}");
                        }
                        // 主存档：tmp 原子写 + Move（崩溃时旧文件完好；首写崩溃时 .tmp 是唯一副本，加载侧可恢复）
                        var tmp = _archivePath + ".tmp";
                        WriteAllTextWithFlush(tmp, json, flushToDisk);
                        File.Move(tmp, _archivePath, overwrite: true);
                        if (flushToDisk) FlushFile(_archivePath);
                    }
                    catch (Exception ex)
                    {
                        try { File.Delete(_archivePath + ".tmp"); } catch { }
                        Console.WriteLine($"[NoteStore] file persist failed: {ex}");
                        try { PersistFailed?.Invoke($"磁盘写入失败：{ex.Message}（请检查磁盘空间与权限）"); } catch { }
                        return false;
                    }
                    // 注册表兼容副本（次要通道：失败记录但不阻塞主存档；selftest 临时 store 跳过，防污染用户键）
                    if (_useRegistry)
                    {
                        try { Registry.SetValue(RegistryRootKey, RegistryJsonKey, json); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[NoteStore] registry persist failed: {ex.Message}");
                        }
                    }
                }
                _lastPersistedAt = DateTime.Now; // 2026-08-14：成功落盘时刻（周期兜底判脏依据）
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NoteStore] serialize failed: {ex}");
                try { PersistFailed?.Invoke($"笔记序列化失败：{ex.Message}"); } catch { }
                return false;
            }
        }

        /// <summary>拍当前内存的最新快照（在 _lock 保护下调用）</summary>
        private PersistedNotes TakeSnapshot() => new()
        {
            Tabs = Tabs.Select(CloneNote).ToList(),
            ActiveTabId = ActiveTabId,
            SavedAt = DateTime.Now,
        };

        private static NoteTab CloneNote(NoteTab tab) => new()
        {
            Id = tab.Id,
            Text = tab.Text,
            CreatedAt = tab.CreatedAt,
            SelectionStart = tab.SelectionStart,
            SelectionLength = tab.SelectionLength,
        };

        private static void WriteAllTextWithFlush(string path, string content, bool flushToDisk)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk);
        }

        private static void FlushFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.Flush(true);
        }

        private PersistedNotes? LoadLatestSnapshot()
        {
            PersistedNotes? fromFile = null;
            PersistedNotes? fromRegistry = null;

            // 主存档 → .bak（上次成功快照）→ .tmp（崩溃恢复）逐级回退：
            // - 主文件损坏（写坏/半写）→ 回退 .bak
            // - 主+备份均缺失但 .tmp 存在 = 写 tmp 成功、Move 前崩溃（首写/重写）→ 恢复 .tmp
            // - 2026-08-14 加固：主文件存在但【旧】、.tmp 更新（写盘被中断残留）→ 用 .tmp 并补 Move。
            //   此前只处理"主文件缺失"场景，导致关机/断电瞬间中断的写盘（tmp 写完 Move 前进程被杀）
            //   在下次启动时被忽略，静默丢失最新快照。
            fromFile = TryLoadFile(_archivePath);
            if (fromFile == null) fromFile = TryLoadFile(_archivePath + ".bak");
            var tmpPath = _archivePath + ".tmp";
            var fromTmp = TryLoadFile(tmpPath);
            if (fromTmp != null && (fromFile == null || fromTmp.SavedAt > fromFile.SavedAt))
            {
                fromFile = fromTmp;
                try { File.Move(tmpPath, _archivePath, overwrite: true); } catch { }
            }

            if (_useRegistry)
            {
                try
                {
                    var regJson = Registry.GetValue(RegistryRootKey, RegistryJsonKey, null) as string;
                    if (!string.IsNullOrEmpty(regJson))
                    {
                        fromRegistry = JsonSerializer.Deserialize<PersistedNotes>(regJson, JsonOpts);
                    }
                }
                catch { }
            }

            var chosen = (fromFile, fromRegistry) switch
            {
                (not null, not null) => fromFile.SavedAt >= fromRegistry.SavedAt ? fromFile : fromRegistry,
                (not null, _) => fromFile,
                (_, not null) => fromRegistry,
                _ => null,
            };
            // 2026-08-14：启动审计日志——各副本 SavedAt 对比（日后丢数据时能快速定位加载了哪个副本、各副本新旧）
            try
            {
                Console.WriteLine($"[NoteStore] load-audit main={TryLoadFile(_archivePath)?.SavedAt} bak={TryLoadFile(_archivePath + ".bak")?.SavedAt} tmp={TryLoadFile(_archivePath + ".tmp")?.SavedAt} registry={fromRegistry?.SavedAt} chosen={chosen?.SavedAt}");
            }
            catch { }
            return chosen;
        }

        private static PersistedNotes? TryLoadFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonSerializer.Deserialize<PersistedNotes>(File.ReadAllText(path), JsonOpts);
                }
            }
            catch { }
            return null;
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string RegistryRootKey =>
            $@"HKEY_CURRENT_USER\{RegistryRoot}";

        // ---- 工具 ----

        private static (int Start, int Length) ClampedRange(int start, int length, string text)
        {
            var len = text.Length;
            var s = Math.Clamp(start, 0, len);
            var l = Math.Clamp(length, 0, len - s);
            return (s, l);
        }

        private void ClampSelectionFor(Guid id)
        {
            var index = Tabs.FindIndex(t => t.Id == id);
            if (index < 0) return;
            var (s, l) = ClampedRange(Tabs[index].SelectionStart ?? 0, Tabs[index].SelectionLength ?? 0, Tabs[index].Text);
            Tabs[index].SelectionStart = s;
            Tabs[index].SelectionLength = l;
        }

        private static string? FirstMeaningfulLine(string text)
        {
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length > 0) return line;
            }
            return null;
        }
    }
}
