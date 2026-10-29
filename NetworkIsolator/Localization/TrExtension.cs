using System.Windows.Data;
using System.Windows.Markup;

namespace NetworkIsolator.Localization;

/// <summary>XAML shortcut: <c>Text="{loc:Tr Nav_Apps}"</c> shows the text in the current language and follows language changes.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension(string key) => Key = key;

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
