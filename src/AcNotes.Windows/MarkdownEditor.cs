using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace AcNotes.Windows
{
    /// <summary>Markdown 工具栏命令（与 editor.html 的 applyCommand 命令名对应）</summary>
    public enum MarkdownCommand
    {
        Bold, Italic, Strikethrough, InlineCode, Link,
        Quote, UnorderedList, OrderedList, TodoList, Timestamp,
    }

    /// <summary>
    /// Tiptap WYSIWYG 编辑器封装（WebView2 宿主）。
    /// 对应 macOS MarkdownEngine 的实时渲染：内容为 Markdown（存储格式不变），
    /// 视觉上完全 WYSIWYG（粗体真粗体、勾选框真控件，无标记符）。
    /// </summary>
    public sealed class MarkdownTiptapEditor
    {
        private readonly WebView2 _webView = new()
        {
            // 对齐官网 --editor #0f1014
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0F, 0x10, 0x14),
        };
        private bool _ready;
        private string _pendingMarkdown = "";
        private int? _pendingFrom;
        private int? _pendingTo;

        public event Action? ContentChanged;
        public event Action? SelectionChanged;
        public event Action<string>? ContentPayloadReceived;
        public event Action<int, int>? SelectionPayloadReceived;

        public UIElement View => _webView;
        public bool IsReady => _ready;

        /// <summary>
        /// 控制可见性：WebView2 是独立子 HWND，不受主窗口 Clip(mask) 裁剪，
        /// 紧凑/动画状态必须隐藏，否则编辑器矩形会裸露在 mask 外（黑块）。
        /// </summary>
        public void SetVisible(bool visible) =>
            _webView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        public MarkdownTiptapEditor()
        {
            _webView.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                    _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
                    // 本地资源经虚拟主机映射加载（2026-08-05 本地化实测：file:// 页面加载 file:// 的
                    // ES module 被 Chromium CORS 拦截（origin 'null'），esm.sh CDN 改为本地 vendor/ 后必须
                    // 走 https://appassets.example/ 同源加载——WebView2 官方推荐方案，不触发真实网络请求）
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "appassets.example",
                        AppContext.BaseDirectory,
                        Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                    _webView.CoreWebView2.Navigate("https://appassets.example/editor.html");
                }
            };
            _webView.NavigationCompleted += async (_, e) =>
            {
                Console.WriteLine($"[editor] NavigationCompleted success={e.IsSuccess} status={e.WebErrorStatus}");
                // 等待编辑器 JS 就绪
                for (int i = 0; i < 50 && !_ready; i++)
                {
                    await Task.Delay(100);
                    try
                    {
                        var ok = await _webView.ExecuteScriptAsync(
                            "JSON.stringify({ready: window.__ready, hasEditor: !!window.__editor, title: document.title, len: document.body ? document.body.innerHTML.length : -1})");
                        // ExecuteScriptAsync 返回值是 JSON 字符串字面量（\"ready\" 带转义），
                        // 需先 Deserialize<string> 解包，再解析内层对象
                        try
                        {
                            var inner = JsonSerializer.Deserialize<string>(ok);
                            if (!string.IsNullOrEmpty(inner))
                            {
                                var el = JsonSerializer.Deserialize<JsonElement>(inner);
                                _ready = el.ValueKind == JsonValueKind.Object
                                    && el.TryGetProperty("ready", out var r) && r.GetBoolean();
                            }
                            else _ready = false;
                        }
                        catch { _ready = false; }
                        if (i % 5 == 0 || _ready) Console.WriteLine($"[editor] ready-check[{i}] = {ok}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[editor] ready-check err: {ex.Message}");
                    }
                }
                if (_ready)
                {
                    FlushPending();
                    ContentChanged?.Invoke();
                }
            };
        }

        public async Task InitAsync()
        {
            // 环境异常（强杀后 msedgewebview2 残留锁 EBWebView 等）时 EnsureCoreWebView2Async 可能挂起——
            // 限时 8 秒返回，不阻塞 OnLoaded 后续流程（2026-08-04 实测：残留进程锁目录 → 10s 未就绪）
            try
            {
                var task = _webView.EnsureCoreWebView2Async();
                var done = await Task.WhenAny(task, Task.Delay(8000));
                if (done != task) Console.WriteLine("[editor] EnsureCoreWebView2Async TIMEOUT (env locked?)");
            }
            catch (Exception ex) { Console.WriteLine("[editor] InitAsync failed: " + ex.Message); }
        }

        private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.String)
                {
                    switch (root.GetString())
                    {
                        case "content":
                            ContentChanged?.Invoke();
                            break;
                        case "selection":
                            SelectionChanged?.Invoke();
                            break;
                    }
                    return;
                }

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeProperty)
                    || typeProperty.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                switch (typeProperty.GetString())
                {
                    case "content":
                        if (root.TryGetProperty("html", out var htmlProperty)
                            && htmlProperty.ValueKind == JsonValueKind.String)
                        {
                            ContentPayloadReceived?.Invoke(htmlProperty.GetString() ?? "");
                        }
                        break;
                    case "selection":
                        if (root.TryGetProperty("from", out var fromProperty)
                            && root.TryGetProperty("to", out var toProperty)
                            && fromProperty.ValueKind == JsonValueKind.Number
                            && toProperty.ValueKind == JsonValueKind.Number)
                        {
                            SelectionPayloadReceived?.Invoke(fromProperty.GetInt32(), toProperty.GetInt32());
                        }
                        break;
                }
            }
            catch
            {
                // Keep the old string bridge working if editor.html is ever cached.
            }
        }

        private void FlushPending()
        {
            if (_pendingMarkdown != null)
            {
                var md = _pendingMarkdown;
                var from = _pendingFrom;
                var to = _pendingTo;
                _pendingMarkdown = "";
                _pendingFrom = null;
                _pendingTo = null;
                _ = SetHtmlAsync(md, from, to);
            }
        }

        // ---- 内容（存储格式：HTML，Tiptap 原生 getHTML/setContent）----

        public async Task<string> GetHtmlAsync()
        {
            // ⚠️ CoreWebView2 未创建时 ExecuteScriptAsync 永久挂起（不抛异常）——未就绪直接返回缓存，
            // 防 SaveEditorStateAsync/CreateNoteAsync 卡死（2026-08-04：残留进程锁环境 → 面板不展开）
            if (!_ready) return _pendingMarkdown;
            try
            {
                // ExecuteScriptAsync 返回合法 JSON 字符串（中文会转义为 \uXXXX），
                // 必须 Deserialize<string> 才能得到真实 HTML
                var json = await _webView.ExecuteScriptAsync("window.__notch.getHTML()");
                return JsonSerializer.Deserialize<string>(json) ?? "";
            }
            catch
            {
                return _pendingMarkdown;
            }
        }

        /// <summary>编辑器实例是否存在（真实就绪判据，防 pending 假阳性）</summary>
        public async Task<bool> EditorExistsAsync()
        {
            try
            {
                var r = await _webView.ExecuteScriptAsync("window.__editor ? true : false");
                return r.Contains("true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public Task SetHtmlAsync(string html, int? from = null, int? to = null)
        {
            if (!_ready)
            {
                _pendingMarkdown = html;
                _pendingFrom = from;
                _pendingTo = to;
                return Task.CompletedTask;
            }
            var js = $"window.__notch.setContent({JsonSerializer.Serialize(html)}, {from?.ToString() ?? "null"}, {to?.ToString() ?? "null"})";
            return Execute(js);
        }

        public async Task<(int From, int To)> GetSelectionAsync()
        {
            if (!_ready) return (0, 0); // 同 GetHtmlAsync：未就绪不挂起
            try
            {
                // ExecuteScriptAsync 返回对象 JSON（无 stringify，避免双重转义）
                var json = await _webView.ExecuteScriptAsync("window.__notch.getSelection()");
                var el = JsonSerializer.Deserialize<JsonElement>(json);
                if (el.ValueKind != JsonValueKind.Object) return (0, 0);
                return (el.GetProperty("from").GetInt32(), el.GetProperty("to").GetInt32());
            }
            catch
            {
                return (0, 0);
            }
        }

        public Task SetSelectionAsync(int from, int? to = null)
        {
            var js = $"window.__notch.setContent(window.__notch.getHTML(), {from}, {to?.ToString() ?? "null"})";
            return Execute(js);
        }

        public Task FocusAsync() => Execute("window.__notch.focus()");

        // ---- 命令（对应 macOS 10 命令 / editor.html applyCommand）----

        public Task ApplyCommandAsync(MarkdownCommand command)
        {
            string cmd = command switch
            {
                MarkdownCommand.Bold => "bold",
                MarkdownCommand.Italic => "italic",
                MarkdownCommand.Strikethrough => "strike",
                MarkdownCommand.InlineCode => "code",
                MarkdownCommand.Link => "link",
                MarkdownCommand.Quote => "blockquote",
                MarkdownCommand.UnorderedList => "bulletList",
                MarkdownCommand.OrderedList => "orderedList",
                MarkdownCommand.TodoList => "taskList",
                MarkdownCommand.Timestamp => "timestamp",
                _ => "bold",
            };
            return Execute($"window.__notch.applyCommand('{cmd}')");
        }

        public Task ClearAsync() => Execute("window.__notch.clear()");

        private Task Execute(string js)
        {
            if (!_ready) return Task.CompletedTask;
            return _webView.ExecuteScriptAsync(js);
        }
    }
}
