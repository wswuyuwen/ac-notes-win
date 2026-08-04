using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// 多标签笔记存储。双通道持久化：notes.json 主存档 + 注册表兼容副本，
    /// 加载时按 savedAt 取较新快照（对应 macOS NoteStore 的 defaults/archive 双通道）。
    /// </summary>
    public sealed class NoteStore
    {
        private const string RegistryRoot = @"Software\AcNotes";
        private const string RegistryJsonKey = "NotesJsonV1";
        private const double SaveDelaySeconds = 0.18;

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
        private readonly object _lock = new();
        private System.Threading.Timer? _saveTimer;
        private bool _dirty;
        private DeletedNote? _recentlyDeleted;

        public List<NoteTab> Tabs { get; private set; } = new();
        public Guid ActiveTabId { get; private set; }

        public NoteStore()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _archivePath = Path.Combine(appData, "AcNotes", "notes.json");

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

            PersistNow(); // 首次落盘，保证双通道同步
        }

        public string Text => Tabs[ActiveIndex].Text;

        public int ActiveIndex => Tabs.FindIndex(t => t.Id == ActiveTabId) is var i && i >= 0 ? i : 0;

        public void UpdateText(string nextText)
        {
            if (Tabs[ActiveIndex].Text == nextText) return;
            Tabs[ActiveIndex].Text = nextText;
            ClampSelectionFor(ActiveTabId);
            ScheduleSave();
        }

        public void AddTab()
        {
            var tab = NoteTab.New();
            Tabs.Add(tab);
            ActiveTabId = tab.Id;
            ScheduleSave();
        }

        public void RemoveActiveTab() => RemoveTab(ActiveTabId);

        public void RemoveTab(Guid id)
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
            ScheduleSave();
        }

        public bool CanRestoreDeletedNote => _recentlyDeleted != null;

        public void RestoreLastDeletedTab()
        {
            if (_recentlyDeleted == null) return;
            var d = _recentlyDeleted;
            var insertIndex = Math.Clamp(d.Index, 0, Tabs.Count);
            Tabs.Insert(insertIndex, d.Tab);
            ActiveTabId = d.Tab.Id;
            _recentlyDeleted = null;
            ScheduleSave();
        }

        public void SelectTab(Guid id)
        {
            if (!Tabs.Any(t => t.Id == id) || id == ActiveTabId) return;
            ActiveTabId = id;
            ScheduleSave();
        }

        public void UpdateSelection(Guid id, int start, int length)
        {
            var index = Tabs.FindIndex(t => t.Id == id);
            if (index < 0) return;
            var (s, l) = ClampedRange(start, length, Tabs[index].Text);
            if (Tabs[index].SelectionStart == s && Tabs[index].SelectionLength == l) return;
            Tabs[index].SelectionStart = s;
            Tabs[index].SelectionLength = l;
            ScheduleSave();
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

        public void Flush()
        {
            lock (_lock)
            {
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _saveTimer = null;
            }
            PersistNow();
        }

        // ---- 持久化 ----

        private void ScheduleSave()
        {
            lock (_lock)
            {
                _dirty = true;
                _saveTimer ??= new Timer(_ => PersistNow(), null, 0, Timeout.Infinite);
                _saveTimer.Change(TimeSpan.FromSeconds(SaveDelaySeconds), Timeout.InfiniteTimeSpan);
            }
        }

        private void PersistNow()
        {
            PersistedNotes snapshot;
            lock (_lock)
            {
                _dirty = false;
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _saveTimer = null;
                snapshot = new PersistedNotes
                {
                    Tabs = Tabs,
                    ActiveTabId = ActiveTabId,
                    SavedAt = DateTime.Now,
                };
            }

            try
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOpts);
                // 主存档（原子写）
                var dir = Path.GetDirectoryName(_archivePath)!;
                Directory.CreateDirectory(dir);
                var tmp = _archivePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _archivePath, overwrite: true);
                // 注册表兼容副本
                try { Registry.SetValue(RegistryRootKey, RegistryJsonKey, json); } catch { }
            }
            catch { /* 双通道任一失败不影响另一通道 */ }
        }

        private PersistedNotes? LoadLatestSnapshot()
        {
            PersistedNotes? fromFile = null;
            PersistedNotes? fromRegistry = null;

            try
            {
                if (File.Exists(_archivePath))
                {
                    fromFile = JsonSerializer.Deserialize<PersistedNotes>(File.ReadAllText(_archivePath), JsonOpts);
                }
            }
            catch { }

            try
            {
                var regJson = Registry.GetValue(RegistryRootKey, RegistryJsonKey, null) as string;
                if (!string.IsNullOrEmpty(regJson))
                {
                    fromRegistry = JsonSerializer.Deserialize<PersistedNotes>(regJson, JsonOpts);
                }
            }
            catch { }

            return (fromFile, fromRegistry) switch
            {
                (not null, not null) => fromFile.SavedAt >= fromRegistry.SavedAt ? fromFile : fromRegistry,
                (not null, _) => fromFile,
                (_, not null) => fromRegistry,
                _ => null,
            };
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
