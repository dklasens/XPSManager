using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace XpsManager.App;

/// <summary>
/// One label / value line in the Activity view: muted label on the left, mono value on the right.
/// Detail becomes the tooltip; Warn turns the value amber; Indent marks a sub-part of the row above.
/// </summary>
public sealed class Fact : Grid
{
    public static readonly DependencyProperty LabelProperty = Register(nameof(Label), "");
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), "—");
    public static readonly DependencyProperty DetailProperty = Register(nameof(Detail), null);
    public static readonly DependencyProperty WarnProperty = DependencyProperty.Register(nameof(Warn), typeof(bool), typeof(Fact),
        new PropertyMetadata(false, (d, _) => ((Fact)d).Paint()));
    public static readonly DependencyProperty IndentProperty = DependencyProperty.Register(nameof(Indent), typeof(bool), typeof(Fact),
        new PropertyMetadata(false, (d, e) => ((Fact)d)._label.Margin = new Thickness((bool)e.NewValue ? 12 : 0, 0, 12, 0)));

    readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBlock _value = new() { HorizontalAlignment = HorizontalAlignment.Right };

    public Fact()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Margin = new Thickness(0, 0, 0, 5);
        Background = Brushes.Transparent; // so the whole row shows the tooltip
        _label.SetResourceReference(StyleProperty, "Description");
        _label.TextWrapping = TextWrapping.NoWrap;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _value.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
        _value.SetResourceReference(TextBlock.FontSizeProperty, "TextSmall");
        _value.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_value, 1);
        Children.Add(_label);
        Children.Add(_value);
        _label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Label)) { Source = this });
        _value.SetBinding(TextBlock.TextProperty, new Binding(nameof(Value)) { Source = this });
        SetBinding(ToolTipProperty, new Binding(nameof(Detail)) { Source = this });
        ToolTipService.SetInitialShowDelay(this, 600);
        Paint();
    }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Detail { get => (string?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public bool Warn { get => (bool)GetValue(WarnProperty); set => SetValue(WarnProperty, value); }
    public bool Indent { get => (bool)GetValue(IndentProperty); set => SetValue(IndentProperty, value); }

    void Paint() => _value.SetResourceReference(TextBlock.ForegroundProperty, Warn ? "Warn" : "Ink");

    static DependencyProperty Register(string name, string? fallback) =>
        DependencyProperty.Register(name, typeof(string), typeof(Fact), new PropertyMetadata(fallback, (d, _) =>
        {
            var fact = (Fact)d;
            AutomationProperties.SetName(fact, $"{fact.Label}: {fact.Value}");
            AutomationProperties.SetHelpText(fact, fact.Detail ?? "");
        }));
}
