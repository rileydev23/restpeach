using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using RestPeach.Core.Http;
using RestPeach.ViewModels;

namespace RestPeach.Views;

/// <summary>One suggestion: a field (with its arguments and type), a keyword or an inline fragment.</summary>
internal sealed class GqlCompletion(string text, string detail, string? description, double priority) : ICompletionData
{
    public IImage? Image => null;
    public string Text => text;
    public object Content => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new TextBlock { Text = text },
            new TextBlock { Text = detail, Foreground = new SolidColorBrush(Color.Parse("#8a8d93")), FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
        },
    };
    public object Description => description is { Length: > 0 } d ? detail + "\n\n" + d : detail;
    public double Priority => priority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, text);
}

/// <summary>
/// Completion for the GraphQL body once the schema is loaded: typing the start of a word, or Ctrl+Space, lists the
/// fields of the type the cursor is in (the operations at the top level).
/// </summary>
internal static class GraphQLCompletion
{
    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static void Attach(TextEditor editor, Func<RequestTab?> tab)
    {
        CompletionWindow? window = null;

        void Show(bool asked)
        {
            if (window is not null || tab() is not { IsGraphQLBody: true } t) return;
            if (t.Schema is not { } schema)
            {
                if (asked) t.LoadSchemaCommand.Execute(null);
                return;
            }
            var text = editor.Document.Text;
            var caret = editor.CaretOffset;
            int start = caret;
            while (start > 0 && IsWord(text[start - 1])) start--;
            var items = Items(schema, text, start);
            if (items.Count == 0) return;
            window = new CompletionWindow(editor.TextArea) { StartOffset = start, EndOffset = caret, MinWidth = 320 };
            foreach (var i in items) window.CompletionList.CompletionData.Add(i);
            window.Closed += (_, _) => window = null;
            window.Show();
            if (caret > start) window.CompletionList.SelectItem(text[start..caret]);
        }

        editor.TextArea.TextEntered += (_, e) =>
        {
            // Only at the start of a word: "us" after "{" opens it, typing inside "user" does not reopen it.
            if (e.Text is not { Length: 1 } typed || !(char.IsLetter(typed[0]) || typed[0] == '_')) return;
            var offset = editor.CaretOffset;
            if (offset >= 2 && IsWord(editor.Document.GetCharAt(offset - 2))) return;
            Show(false);
        };
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                Show(true);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private static List<ICompletionData> Items(GraphQLSchema s, string text, int at)
    {
        var list = new List<ICompletionData>();
        var type = s.TypeAt(text, at);
        if (type is null)
        {
            foreach (var (k, root) in new[] { ("query", s.QueryType), ("mutation", s.MutationType), ("subscription", s.SubscriptionType), ("fragment", "") })
                if (root is not null) list.Add(new GqlCompletion(k, root.Length > 0 ? root : "… on Type", null, 1));
            return list;
        }
        if (!s.Types.TryGetValue(type, out var t)) return list;
        foreach (var f in t.Fields)
            list.Add(new GqlCompletion(f.Name, f.Signature + (f.Deprecated ? "  (deprecated)" : ""), f.Description, f.Deprecated ? 0 : 1));
        foreach (var p in t.PossibleTypes)
            list.Add(new GqlCompletion("... on " + p, t.Kind.ToLowerInvariant(), null, 0.5));
        if (t.Kind is "OBJECT" or "INTERFACE" or "UNION") list.Add(new GqlCompletion("__typename", "String!", null, 0.1));
        return list;
    }
}
