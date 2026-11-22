using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

internal sealed class Controls
{
    internal AutomationElement Header, Editor, EditorContainer, Button, History;
    private AutomationElement origin;
    internal List<AutomationElement> Bubbles;
    internal int NodeCount;
    internal string Source, Shapes;

    private static PropertyCondition Is(AutomationProperty p, object value) { return new PropertyCondition(p, value); }
    private static AutomationElement[] Nodes(AutomationElement root, TreeScope scope, Condition filter)
    {
        return root.FindAll(scope, filter).Cast<AutomationElement>().ToArray();
    }
    internal static T Pattern<T>(AutomationElement el, AutomationPattern pattern) where T : class
    {
        object value;
        return el != null && el.TryGetCurrentPattern(pattern, out value) ? value as T : null;
    }
    internal static Controls Scan(Target target)
    {
        if (!target.SameWindow()) throw new InvalidOperationException("目标窗口失效");
        var best = Discover(AutomationElement.FromHandle(target.Hwnd), "HWND");
        // Some QQ builds expose the Chromium tree only from the renderer HWND.
        if (best.Header != null && best.Value != null && best.HasMessageArea) return best;
        var renderers = new List<IntPtr>();
        Native.EnumChildWindows(target.Hwnd, (h, d) => {
            if (Native.Class(h) == "Chrome_RenderWidgetHostHWND" && Native.Visible(h)) renderers.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (var hwnd in renderers)
        {
            var alternate = Discover(AutomationElement.FromHandle(hwnd), "renderer");
            if (alternate.Score > best.Score) best = alternate;
        }
        return best;
    }
    private int Score { get { return (Header != null ? 4 : 0) + (Editor != null ? 4 : 0) +
        (History != null ? 2 : 0) + (Button != null ? 1 : 0) + (Value != null ? 4 : 0) +
        (Bubbles.Count != 0 ? 2 : 0); } }

    private static bool HasClass(AutomationElement el, string name)
    {
        return el.Current.ClassName.Split(' ').Contains(name);
    }
    private static AutomationElement Unique(IEnumerable<AutomationElement> nodes)
    {
        var found = nodes.Take(2).ToArray();
        return found.Length == 1 ? found[0] : null;
    }
    private static AutomationElement[] RawChildren(AutomationElement root)
    {
        var walker = TreeWalker.RawViewWalker;
        var nodes = new List<AutomationElement>();
        for (var node = walker.GetFirstChild(root); node != null && nodes.Count < 200; node = walker.GetNextSibling(node))
            nodes.Add(node);
        return nodes.ToArray();
    }
    private static IEnumerable<AutomationElement> RawDescendants(AutomationElement root, int depth)
    {
        if (depth == 0) yield break;
        foreach (var node in RawChildren(root))
        {
            yield return node;
            foreach (var child in RawDescendants(node, depth - 1)) yield return child;
        }
    }
    private static Controls Discover(AutomationElement root, string source)
    {
        var elements = Nodes(root, TreeScope.Descendants, Condition.TrueCondition);
        var result = new Controls { Source = source, NodeCount = elements.Length, origin = root };
        result.Header = Unique(elements.Where(e => HasClass(e, "chat-header__contact-name")));
        result.Editor = Unique(elements.Where(e => HasClass(e, "qq-msg-editor")));
        result.EditorContainer = result.Editor;
        if (result.Editor == null)
        {
            var input = Unique(elements.Where(e => HasClass(e, "msg-input")));
            if (input != null)
            {
                result.EditorContainer = input;
                var rawEdits = RawDescendants(input, 4).Take(160).Where(e => e.Current.ControlType == ControlType.Edit);
                result.Editor = input.Current.ControlType == ControlType.Edit ? input :
                    Unique(Nodes(input, TreeScope.Descendants, Is(AutomationElement.ControlTypeProperty, ControlType.Edit))) ??
                    Unique(rawEdits) ?? input;
            }
        }
        if (result.Editor != null && result.Value == null)
        {
            var editable = RawDescendants(result.Editor, 4).Take(100).Where(e =>
                e.Current.IsKeyboardFocusable &&
                (Pattern<ValuePattern>(e, ValuePattern.Pattern) != null || Pattern<TextPattern>(e, TextPattern.Pattern) != null));
            result.Editor = Unique(editable) ?? result.Editor;
        }
        result.Button = Unique(Nodes(root, TreeScope.Descendants, new AndCondition(
            Is(AutomationElement.ControlTypeProperty, ControlType.Button),
            new OrCondition(Is(AutomationElement.NameProperty, "发送"), Is(AutomationElement.NameProperty, "Send")))));
        result.History = Unique(elements.Where(e =>
            e.Current.AutomationId == "ml-root" || HasClass(e, "ml-root") || HasClass(e, "message-list")));
        var historyNodes = result.History == null ? elements : Nodes(result.History, TreeScope.Descendants, Condition.TrueCondition);
        result.Bubbles = historyNodes.Where(e => HasClass(e, "msg-content-container")).ToList();
        if (result.Bubbles.Count == 0)
            result.Bubbles = historyNodes.Where(e => HasClass(e, "message-container")).ToList();
        if (result.Bubbles.Count == 0 && result.History != null)
        {
            var rawRows = RawDescendants(result.History, 4).Take(450).ToArray();
            result.Bubbles = rawRows.Where(e => HasClass(e, "msg-content-container")).ToList();
            if (result.Bubbles.Count == 0)
                result.Bubbles = rawRows.Where(e => HasClass(e, "message-container")).ToList();
        }
        result.Shapes = String.Join(",", elements.Select(e => e.Current.ClassName)
            .SelectMany(s => s.Split(' '))
            .Where(s => Regex.IsMatch(s, "^(chat-header|qq-msg|msg-|message-|ml-root)[a-z_0-9-]*$"))
            .Distinct().Take(28));
        return result;
    }

    internal string HeaderIdentity()
    {
        return Header == null ? null : Header.Current.Name.Trim();
    }

    internal bool HasVisibleConnectionWarning()
    {
        var warnings = new[] { "连接断开", "连接已断开", "网络连接断开", "网络连接已断开", "正在重新连接" };
        var condition = new OrCondition(warnings.Select(s =>
            (Condition)Is(AutomationElement.NameProperty, s)).ToArray());
        foreach (var warning in Nodes(origin, TreeScope.Descendants, condition))
        {
            if (warning.Current.IsOffscreen) continue;
            bool status = false;
            for (var node = warning; node != null && !node.Equals(origin); node = TreeWalker.RawViewWalker.GetParent(node))
            {
                if (node.Equals(History) || node.Equals(EditorContainer)) { status = false; break; }
                status |= node.Current.ControlType == ControlType.StatusBar ||
                    Regex.IsMatch(node.Current.ClassName, @"(?:toast|notification|network-status|connection-status|dialog|alert)",
                        RegexOptions.IgnoreCase);
            }
            if (status) return true;
        }
        return false;
    }
    internal ValuePattern Value { get { return Pattern<ValuePattern>(Editor, ValuePattern.Pattern); } }
    internal InvokePattern Invoke { get { return Pattern<InvokePattern>(Button, InvokePattern.Pattern); } }
    internal LegacyEditor.ILegacy Legacy(AutomationElement node) { return LegacyEditor.Find(origin, node); }
    internal string Draft()
    {
        if (Editor == null) return null;
        var text = Pattern<TextPattern>(Editor, TextPattern.Pattern);
        if (text == null) return Value == null ? null : Value.Current.Value;
        // Chromium exposes a terminal block separator even for an empty editor.
        var raw = text.DocumentRange.GetText(4096);
        return raw == null ? null : raw.Replace("\r\n", "\n").TrimEnd('\r', '\n', '\u200B');
    }
    internal bool EmptyDraft()
    {
        if (Editor == null || Draft() != "") return false;
        var classes = Editor.Current.ClassName.Split(' ');
        if (classes.Contains("ProseMirror")) return classes.Contains("is-empty");
        if (Value != null) return Value.Current.Value == "";
        var document = Pattern<TextPattern>(Editor, TextPattern.Pattern);
        return document != null && document.DocumentRange.GetText(4096) == "";
    }
    internal bool EditorFocused()
    {
        if (Editor == null) return false;
        var id = Editor.GetRuntimeId();
        var node = AutomationElement.FocusedElement;
        for (int depth = 0; node != null && depth < 10; depth++, node = TreeWalker.RawViewWalker.GetParent(node))
            if (node.GetRuntimeId().SequenceEqual(id)) return true;
        return false;
    }
    internal IEnumerable<AutomationElement> EditorTopology()
    {
        if (EditorContainer == null) yield break;
        foreach (var node in new[] { EditorContainer }.Concat(RawDescendants(EditorContainer, 5).Take(35)))
            yield return node;
        for (var node = TreeWalker.RawViewWalker.GetParent(EditorContainer); node != null;
             node = TreeWalker.RawViewWalker.GetParent(node))
        {
            yield return node;
            if (node.Current.NativeWindowHandle != 0 || node.GetRuntimeId().SequenceEqual(origin.GetRuntimeId())) yield break;
        }
    }
    internal IntPtr NativeEditor(Target target)
    {
        if (Editor == null) return IntPtr.Zero;
        var hwnd = new IntPtr(Editor.Current.NativeWindowHandle);
        // A renderer surface is shared by multiple virtual controls.
        return Native.IsDedicatedEdit(target.Hwnd, hwnd) ? hwnd : IntPtr.Zero;
    }
    internal EditorAccess Input()
    {
        return new EditorAccess(Editor, Value, Value == null ? LegacyEditor.Find(origin, Editor) : null);
    }
    internal bool HasMessageArea { get { return History != null || Bubbles.Count != 0; } }
    internal string Describe()
    {
        var value = Value;
        return String.Format("source={0} nodes={1} header={2} history={3} editor={4} value={5} text={6} send={7} invoke={8} rawHistoryChildren={9} bubbleCandidates={10} editorType={11} rawEditorChildren={12} legacy={13}",
            Source, NodeCount, State(Header), State(History ?? Bubbles.FirstOrDefault()), State(Editor),
            value == null ? "missing" : value.Current.IsReadOnly ? "read-only" : "advertised",
            Pattern<TextPattern>(Editor, TextPattern.Pattern) == null ? "missing" : "advertised",
            State(Button), Invoke == null ? "missing" : "advertised",
            History == null ? 0 : RawChildren(History).Length,
            Bubbles.Count, Editor == null ? "missing" : Editor.Current.ControlType.ProgrammaticName,
            Editor == null ? 0 : RawChildren(Editor).Length, LegacyStatus(value));
    }
    private string LegacyStatus(ValuePattern value)
    {
        if (value != null) return "not-needed";
        if (Editor == null) return "missing";
        try
        {
            var legacy = LegacyEditor.Find(origin, Editor);
            return legacy == null ? "missing" : LegacyEditor.Writable(legacy) ? "eligible" :
                String.Format("not-editable(role=0x{0:X},state=0x{1:X})", legacy.CurrentRole(), legacy.CurrentState());
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            return String.Format("error(0x{0:X8})", ex.HResult);
        }
    }
    private static string State(AutomationElement node)
    {
        return node == null ? "missing" : (node.Current.IsEnabled ? "enabled" : "disabled") +
            (node.Current.IsOffscreen ? "/offscreen" : "/onscreen");
    }
    internal string RawShape(AutomationElement root)
    {
        if (root == null) return "missing";
        return String.Join(" ", RawDescendants(root, 6).Take(40).GroupBy(e =>
            e.Current.ControlType.ProgrammaticName + (Pattern<ValuePattern>(e, ValuePattern.Pattern) == null ? "" : "/value") +
            (Pattern<TextPattern>(e, TextPattern.Pattern) == null ? "" : "/text"))
            .Select(g => g.Key + "=" + g.Count()));
    }
    private static IEnumerable<string> ReadBubble(AutomationElement row, int depth)
    {
        if (row.Current.ControlType == ControlType.Text && row.Current.Name.Length != 0)
        {
            yield return row.Current.Name;
            yield break;
        }
        if (depth == 0) yield break;
        foreach (var child in RawChildren(row))
            foreach (var text in ReadBubble(child, depth - 1)) yield return text;
    }
    internal ReplyRow[] ReplyRows()
    {
        if (History == null) throw new InvalidOperationException("消息列表未就绪");
        return Bubbles.Skip(Math.Max(0, Bubbles.Count - 40)).Select(bubble => {
            var id = bubble.GetRuntimeId();
            if (id == null || id.Length == 0) throw new InvalidOperationException("消息气泡缺少 UIA 标识");
            var parts = ReadBubble(bubble, 6).Take(200).ToArray();
            return new ReplyRow(String.Join(".", id), parts, OwnMessage(bubble));
        }).ToArray();
    }
    internal string ExternalFieldEvidence(string key, string selfId, string peerId)
    {
        var bubble = Bubbles.Single(e => String.Join(".", e.GetRuntimeId()) == key);
        var roots = new List<AutomationElement> { bubble };
        var walker = TreeWalker.RawViewWalker;
        for (var node = walker.GetParent(bubble); node != null && !node.Equals(History); node = walker.GetParent(node))
        {
            if (!HasClass(node, "message-container") && !HasClass(node, "message-container--self")) continue;
            roots.Add(node);
            break;
        }
        var rowFields = roots.SelectMany(e => new[] { e }.Concat(RawDescendants(e, 3).Take(60))).Distinct().ToArray();
        var headerFields = new[] { Header }.Concat(RawDescendants(Header, 2).Take(20)).ToArray();
        return "row-" + FieldMatches(rowFields, selfId, peerId) + " header-" + FieldMatches(headerFields, selfId, peerId);
    }
    private static string FieldMatches(AutomationElement[] elements, string selfId, string peerId)
    {
        var p = new[] { AutomationElement.NameProperty, AutomationElement.AutomationIdProperty,
            AutomationElement.HelpTextProperty, AutomationElement.ItemStatusProperty, AutomationElement.ItemTypeProperty };
        var found = new List<string>();
        foreach (var property in p)
        {
            var values = elements.Select(e => e.GetCurrentPropertyValue(property, true) as string ?? "");
            foreach (var value in values)
            {
                if (Regex.IsMatch(value, @"(?<![0-9])" + selfId + @"(?![0-9])")) found.Add("self." + property.ProgrammaticName);
                if (Regex.IsMatch(value, @"(?<![0-9])" + peerId + @"(?![0-9])")) found.Add("peer." + property.ProgrammaticName);
            }
        }
        return "fields=" + (found.Count == 0 ? "no-known-id" : String.Join(",", found.Distinct())) + " nodes=" + elements.Length;
    }
    private static bool? OwnMessage(AutomationElement bubble)
    {
        var walker = TreeWalker.RawViewWalker;
        for (var node = bubble; node != null; node = walker.GetParent(node))
        {
            if (HasClass(node, "message-container--self")) return true;
            if (HasClass(node, "message-container")) return false;
            if (HasClass(node, "ml-root")) break;
        }
        return null;
    }
    internal MessageSnapshot Messages(params string[] markers)
    {
        var uniqueMarkers = markers.Distinct().ToArray();
        var rows = Bubbles;
        if (rows.Count == 0 && History != null)
        {
            // QQ's ml-root variant exposes flat unnamed bubble groups with text descendants.
            rows = RawChildren(History).Where(e =>
                (e.Current.ControlType == ControlType.Group || e.Current.ControlType == ControlType.ListItem)
                && String.IsNullOrEmpty(e.Current.Name)).ToList();
        }
        var snapshot = new MessageSnapshot { AreaPresent = HasMessageArea, Rows = rows.Count };
        foreach (var bubble in rows.Skip(Math.Max(0, rows.Count - 40)))
        {
            var parts = ReadBubble(bubble, 6).Take(200).ToArray();
            var text = String.Concat(parts).Trim();
            if (text.Length == 0) continue;
            snapshot.TextRows++;
            var hash = MessageSnapshot.Hash(text);
            int count;
            snapshot.Counts.TryGetValue(hash, out count);
            snapshot.Counts[hash] = count + 1;
            foreach (var marker in uniqueMarkers)
            {
                var expected = MessageSnapshot.Normalize(marker);
                if (expected.Length == 0) continue;
                if (MessageSnapshot.Normalize(text).IndexOf(expected, StringComparison.Ordinal) >= 0 ||
                    parts.Any(part => MessageSnapshot.Normalize(part) == expected))
                {
                    int matches;
                    snapshot.MarkerCounts.TryGetValue(marker, out matches);
                    snapshot.MarkerCounts[marker] = matches + 1;
                }
                if (MessageSnapshot.Normalize(text) == expected || parts.Any(part => MessageSnapshot.Normalize(part) == expected))
                    snapshot.Markers[marker] = "exact";
                else if (!snapshot.Markers.ContainsKey(marker) &&
                    MessageSnapshot.Normalize(text).IndexOf(expected, StringComparison.Ordinal) >= 0)
                    snapshot.Markers[marker] = "within-bubble";
            }
        }
        return snapshot;
    }
}

internal sealed class EditorAccess
{
    private readonly AutomationElement editor;
    private readonly ValuePattern value;
    private readonly LegacyEditor.ILegacy legacy;
    internal EditorAccess(AutomationElement editor, ValuePattern value, LegacyEditor.ILegacy legacy)
    {
        this.editor = editor;
        this.value = value;
        this.legacy = legacy;
    }
    internal bool Writable
    {
        get { return editor != null && editor.Current.IsEnabled &&
            (value != null ? !value.Current.IsReadOnly : LegacyEditor.Writable(legacy)); }
    }
    internal string Method { get { return value != null ? "ValuePattern" : "LegacyIAccessiblePattern"; } }
    internal string Read()
    {
        if (value != null) return value.Current.Value;
        if (!LegacyEditor.Writable(legacy)) return null;
        var legacyText = legacy.CurrentValue();
        var document = Controls.Pattern<TextPattern>(editor, TextPattern.Pattern);
        // Conflicting channels could hide a draft. Treat unknown content as unsafe.
        if (document != null && document.DocumentRange.GetText(4096).TrimEnd('\r', '\n') != legacyText) return null;
        return legacyText;
    }
    internal void Set(string text)
    {
        if (value != null) value.SetValue(text);
        else System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(legacy.SetValue(text));
    }
}

internal sealed class MessageSnapshot
{
    internal bool AreaPresent;
    internal int Rows, TextRows;
    internal readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
    internal readonly Dictionary<string, string> Markers = new Dictionary<string, string>();
    internal readonly Dictionary<string, int> MarkerCounts = new Dictionary<string, int>();
    internal int MarkerRows(string marker)
    {
        int result;
        return MarkerCounts.TryGetValue(marker, out result) ? result : 0;
    }
    internal static string Normalize(string text)
    {
        return Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"[\s\u200B\u200E\u200F\uFEFF]+", "");
    }
    internal static string Hash(string message)
    {
        using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }
    internal string Evidence(string message)
    {
        string kind;
        return Markers.TryGetValue(message, out kind) ? kind : Counts.ContainsKey(Hash(message)) ? "exact" : "missing";
    }
    internal bool Contains(string message) { return Evidence(message) != "missing"; }
    internal int AddedComparedTo(MessageSnapshot first)
    {
        return Counts.Sum(pair => {
            int count;
            first.Counts.TryGetValue(pair.Key, out count);
            return Math.Max(0, pair.Value - count);
        });
    }
}

internal sealed class Binding
{
    internal readonly Target Target;
    private readonly string header;
    private readonly int[] headerId;
    internal readonly bool Self;
    internal string HeaderProof { get { return MessageSnapshot.Hash(header + "\n" + String.Join(".", headerId)); } }
    // The operator verifies QQ numbers locally. The program stores no account number.
    internal Binding(Target target, bool self)
    {
        Target = target;
        Self = self;
        var controls = Controls.Scan(target);
        header = controls.HeaderIdentity();
        if (String.IsNullOrWhiteSpace(header))
            throw new InvalidOperationException("此窗口没有唯一的 QQ 会话头。请打开独立聊天窗口。");
        headerId = controls.Header.GetRuntimeId();
    }
    internal bool Matches(Controls controls)
    {
        return Target.SameWindow() && Native.Title(Target.Hwnd) == Target.Title && controls.HeaderIdentity() == header &&
            controls.Header.GetRuntimeId().SequenceEqual(headerId);
    }
    internal Controls RequireMatch()
    {
        var controls = Controls.Scan(Target);
        if (!Matches(controls)) throw new InvalidOperationException("当前会话无法复核");
        return controls;
    }
}
