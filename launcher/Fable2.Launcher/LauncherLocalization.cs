using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Fable2Launcher;

/// <summary>
/// Updates only user-declared WPF text, without traversing framework templates
/// or forcing visual-tree generation. The previous visual/logical mixed walk
/// could execute while the window was still constructing; it also touched
/// internal generated visuals and risked startup failure.
/// </summary>
public static class LauncherLocalization
{
    private sealed class DisplayState
    {
        public string Original = "";
        public string LastRendered = "";
    }

    private sealed class ElementStates
    {
        public Dictionary<string, DisplayState> Properties { get; } = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<DependencyObject, ElementStates> States = new();

    public static void Apply(DependencyObject root)
    {
        // Snapshot declared children before updating the current element.
        // This prevents changes to WPF inline collections / item containers from
        // invalidating any active collection enumeration.
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject node = pending.Pop();
            if (!visited.Add(node)) continue;
            List<DependencyObject> children = DeclaredChildren(node);
            LocalizeElement(node);
            for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
        }
    }

    private static List<DependencyObject> DeclaredChildren(DependencyObject node)
    {
        var children = new List<DependencyObject>();
        if (node is Panel panel)
        {
            foreach (UIElement child in panel.Children) children.Add(child);
        }
        else if (node is Decorator decorator)
        {
            if (decorator.Child != null) children.Add(decorator.Child);
        }
        else if (node is ContentControl content)
        {
            if (content.Content is DependencyObject child) children.Add(child);
            if (content is HeaderedContentControl header && header.Header is DependencyObject head)
                children.Add(head);
        }
        else if (node is ItemsControl items)
        {
            foreach (object item in items.Items)
                if (item is DependencyObject child) children.Add(child);
        }
        else if (node is TextBlock text)
        {
            // Plain TextBlock.Text is handled as one string. Explicit Run / Hyperlink
            // elements in TextBlock.Inlines must instead be traversed individually.
            if (text.ReadLocalValue(TextBlock.TextProperty) == DependencyProperty.UnsetValue)
                foreach (Inline child in text.Inlines) children.Add(child);
        }
        else if (node is Span span)
        {
            foreach (Inline child in span.Inlines) children.Add(child);
        }
        return children;
    }

    private static void LocalizeElement(DependencyObject node)
    {
        if (node is Window window)
            Localize(node, "Title", () => window.Title, value => window.Title = value);

        if (node is TextBlock text &&
            text.ReadLocalValue(TextBlock.TextProperty) != DependencyProperty.UnsetValue)
            Localize(node, "Text", () => text.Text, value => text.Text = value);

        if (node is Run run)
            Localize(node, "Run", () => run.Text, value => run.Text = value);

        if (node is ContentControl control && control.Content is string)
            Localize(node, "Content", () => control.Content as string, value => control.Content = value);

        if (node is HeaderedContentControl headControl && headControl.Header is string)
            Localize(node, "Header", () => headControl.Header as string, value => headControl.Header = value);

        if (node is HeaderedItemsControl headItems && headItems.Header is string)
            Localize(node, "Header", () => headItems.Header as string, value => headItems.Header = value);

        if (node is FrameworkElement element)
        {
            if (element.ToolTip is string)
                Localize(node, "ToolTip", () => element.ToolTip as string,
                    value => element.ToolTip = value);
            string automationName = AutomationProperties.GetName(element);
            if (!string.IsNullOrEmpty(automationName))
                Localize(node, "AccessibleName", () => AutomationProperties.GetName(element),
                    value => AutomationProperties.SetName(element, value));
        }
    }

    private static void Localize(DependencyObject node, string property,
        Func<string?> read, Action<string> write)
    {
        string? current = read();
        if (string.IsNullOrEmpty(current)) return;
        ElementStates states = States.GetValue(node, _ => new ElementStates());
        if (!states.Properties.TryGetValue(property, out DisplayState? state))
        {
            state = new DisplayState { Original = current, LastRendered = current };
            states.Properties[property] = state;
        }
        else if (!string.Equals(current, state.LastRendered, StringComparison.Ordinal))
        {
            // Runtime changed this text: treat its new value as the source.
            state.Original = current;
        }
        string localized = LauncherLanguage.T(state.Original);
        if (!string.Equals(current, localized, StringComparison.Ordinal)) write(localized);
        state.LastRendered = localized;
    }
}
