using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ChatAgentBridge_Desktop.Localization;

/// <summary>Reads the app's .resw strings with an explicit language, independent of the view's cached x:Uid values.</summary>
internal static class MrtText
{
    private static readonly ResourceManager ResourceManager = new();
    private static readonly ResourceMap Resources = ResourceManager.MainResourceMap.GetSubtree("Resources");

    public static string Get(string key, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return Get(key, CreateContext(culture));
    }

    private static ResourceContext CreateContext(CultureInfo culture)
    {
        var context = ResourceManager.CreateResourceContext();
        context.QualifierValues["Language"] = culture.Name;
        return context;
    }

    private static string Get(string key, ResourceContext context)
    {
        // PRI stores x:Uid property separators as path segments. Dots inside an
        // attached property's [using:Namespace] qualifier remain literal.
        var bracketStart = key.IndexOf('[');
        var bracketEnd = key.IndexOf(']');
        var path = bracketStart >= 0 && bracketEnd > bracketStart
            ? key[..bracketStart].Replace('.', '/') + key[bracketStart..(bracketEnd + 1)] + key[(bracketEnd + 1)..].Replace('.', '/')
            : key.Replace('.', '/');
        return Resources.TryGetValue(path, context)?.ValueAsString ?? key;
    }

    public static void Refresh(DependencyObject root, CultureInfo culture)
    {
        var context = CreateContext(culture);
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (current is FrameworkElement element)
                ApplyElement(element, culture, context);
            if (current is ComboBox combo)
                foreach (var item in combo.Items.OfType<ComboBoxItem>()) ApplyElement(item, culture, context);
            if (current is RadioButtons radios)
                foreach (var item in radios.Items.OfType<DependencyObject>()) pending.Push(item);
            // Traverse logical item/content collections as well as realized visual
            // children. Hidden child pages and controls inside collapsed containers
            // may not have a complete visual tree when the language is changed.
            if (current is ItemsControl itemsControl)
                foreach (var item in itemsControl.Items.OfType<DependencyObject>()) pending.Push(item);
            // The shell is collapsed while onboarding is visible. Include logical
            // content so labels that have not been templated yet also change.
            if (current is NavigationView navigation)
            {
                foreach (var item in navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<DependencyObject>()) pending.Push(item);
            }
            if (current is Panel panel)
                foreach (var child in panel.Children) pending.Push(child);
            if (current is Border border && border.Child is { } borderChild) pending.Push(borderChild);
            if (current is UserControl userControl && userControl.Content is { } userContent) pending.Push(userContent);
            if (current is ContentControl contentControl && contentControl.Content is DependencyObject content) pending.Push(content);
            var typeInfo = GetTypeInfo(current.GetType());
            if (typeInfo.Header?.GetValue(current) is DependencyObject header)
                pending.Push(header);
            if (typeInfo.ActionButton?.GetValue(current) is DependencyObject actionButton)
                pending.Push(actionButton);
            // Flyout content is not part of the visual tree while its popup is
            // closed, but it still owns x:Uid labels that need to refresh.
            if (typeInfo.Flyout?.GetValue(current) is { } flyout &&
                GetTypeInfo(flyout.GetType()).Content?.GetValue(flyout) is DependencyObject flyoutContent)
                pending.Push(flyoutContent);
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private static void ApplyElement(FrameworkElement element, CultureInfo culture, ResourceContext context)
    {
        var id = LocalizedResourceKey.GetKey(element);
        if (string.IsNullOrWhiteSpace(id))
            id = AutomationProperties.GetAutomationId(element);
        if (string.IsNullOrWhiteSpace(id)) return;

        // x:Uid has no runtime getter. The attached key records it where the
        // automation ID differs or is absent; existing matching IDs remain a
        // convenient fallback. Apply only string-valued resource properties and
        // leave active bindings and nested rich content alone.
        var typeInfo = GetTypeInfo(element.GetType());
        foreach (var property in LocalizedStringProperties)
        {
            typeInfo.Localized.TryGetValue(property, out var info);
            if (info?.CanWrite != true || info.PropertyType != typeof(string) && info.PropertyType != typeof(object) || IsBound(element, property)) continue;
            var currentValue = info.GetValue(element);
            if (currentValue is not null and not string) continue;
            Apply(id, property, context, value => info.SetValue(element, value));
        }
        var automationKey = $"{id}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";
        var automationName = Get(automationKey, context);
        if (!automationName.Equals(automationKey, StringComparison.Ordinal))
            AutomationProperties.SetName(element, automationName);
    }

    /// <summary>Reflection lookups resolved once per control type; Refresh walks the tree on every language change.</summary>
    private sealed class TypeInfo(Type type)
    {
        private static PropertyInfo? Find(Type t, string name) => t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        public PropertyInfo? Header { get; } = Find(type, "Header");
        public PropertyInfo? ActionButton { get; } = Find(type, "ActionButton");
        public PropertyInfo? Flyout { get; } = Find(type, "Flyout");
        public PropertyInfo? Content { get; } = Find(type, "Content");
        public Dictionary<string, PropertyInfo?> Localized { get; } = LocalizedStringProperties.ToDictionary(name => name, name => Find(type, name));
    }

    private static readonly ConcurrentDictionary<Type, TypeInfo> TypeInfos = new();

    private static TypeInfo GetTypeInfo(Type type) => TypeInfos.GetOrAdd(type, static t => new TypeInfo(t));

    private static readonly ConcurrentDictionary<(Type, string), DependencyProperty?> DependencyProperties = new();

    private static readonly string[] LocalizedStringProperties =
        ["Text", "Content", "Header", "PlaceholderText", "Title", "Message", "Description", "Subtitle", "OnContent", "OffContent"];

    private static bool IsBound(FrameworkElement element, string property)
    {
        var dependencyProperty = DependencyProperties.GetOrAdd((element.GetType(), property), static key => FindDependencyProperty(key.Item1, key.Item2));
        return dependencyProperty is not null && element.GetBindingExpression(dependencyProperty) is not null;
    }

    private static DependencyProperty? FindDependencyProperty(Type elementType, string property)
    {
        var propertyName = $"{property}Property";
        for (var type = elementType; type is not null; type = type.BaseType)
        {
            var dependencyProperty = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetValue(null) as DependencyProperty
                ?? type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetValue(null) as DependencyProperty;
            if (dependencyProperty is not null) return dependencyProperty;
        }
        return null;
    }

    private static void Apply(string id, string property, ResourceContext context, Action<string> set)
    {
        var key = $"{id}.{property}";
        var value = Get(key, context);
        if (!value.Equals(key, StringComparison.Ordinal)) set(value);
    }
}
