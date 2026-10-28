using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace AirDrop.App.Localization;

/// <summary>
/// Bindable view of every resource key: <c>LocSource.Instance["Key"]</c>. Raises <c>Item[]</c> when the language
/// changes, so every <see cref="LocExtension"/> binding re-reads its string (no restart).
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource() => Loc.LanguageChanged += (_, _) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));

    public string this[string key] => Loc.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// <c>Text="{l:Loc Nearby_Searching}"</c>: a one-way binding to <see cref="LocSource"/>, live across language switches.
/// Works in templates, styles (Setter.Value) and on any dependency property. <see cref="Upper"/> upper-cases in the UI
/// culture (Turkish i → İ).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public bool Upper { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var b = new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay };
        if (Upper) b.Converter = UpperConverter.Instance;
        return b.ProvideValue(serviceProvider);
    }

    private sealed class UpperConverter : IValueConverter
    {
        public static readonly UpperConverter Instance = new();
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            (value as string)?.ToUpper(Loc.Culture) ?? "";
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => Binding.DoNothing;
    }
}

/// <summary>
/// Formatted rich text from one resource, for sentences whose word order differs between languages:
/// <c>&lt;TextBlock l:Rich.Key="Settings_ExtStep1"/&gt;</c>. Markup in the resource: <c>**bold**</c> and
/// <c>`mono`</c> (MonoFont, slightly smaller). Rebuilt when the language changes.
/// </summary>
public static class Rich
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Rich), new PropertyMetadata(null, (d, _) => { if (d is TextBlock tb) Build(tb); }));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? v) => d.SetValue(KeyProperty, v);

    private static readonly List<WeakReference<TextBlock>> Live = new();

    static Rich() => Loc.LanguageChanged += (_, _) =>
    {
        Live.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Live.ToArray())
            if (w.TryGetTarget(out var tb)) Build(tb, register: false);
    };

    private static void Build(TextBlock tb, bool register = true)
    {
        if (register) Live.Add(new WeakReference<TextBlock>(tb));
        tb.Inlines.Clear();
        var key = GetKey(tb);
        if (string.IsNullOrEmpty(key)) return;
        foreach (var inline in Parse(Loc.Get(key), tb)) tb.Inlines.Add(inline);
    }

    /// <summary>Splits on <c>**…**</c> and <c>`…`</c>; unmatched markers stay literal.</summary>
    internal static IEnumerable<Inline> Parse(string text, FrameworkElement owner)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    if (sb.Length > 0) { yield return new Run(sb.ToString()); sb.Clear(); }
                    yield return new Run(text[(i + 2)..end]) { FontWeight = FontWeights.SemiBold };
                    i = end + 2;
                    continue;
                }
            }
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    if (sb.Length > 0) { yield return new Run(sb.ToString()); sb.Clear(); }
                    var run = new Run(text[(i + 1)..end]);
                    if (owner.TryFindResource("MonoFont") is FontFamily mono) run.FontFamily = mono;
                    run.FontSize = Math.Max(9, (owner is TextBlock t ? t.FontSize : 12.5) - 1);
                    yield return run;
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(text[i]);
            i++;
        }
        if (sb.Length > 0) yield return new Run(sb.ToString());
    }
}
