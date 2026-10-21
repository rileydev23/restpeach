using RestPeach.Core;

namespace RestPeach.Services;

/// <summary>XAML: <c>Text="{l:L rv.send}"</c>. The texts themselves live in <see cref="Strings"/> (Core).</summary>
public sealed class LExtension : Avalonia.Markup.Xaml.MarkupExtension
{
    public LExtension(string key) => Key = key;
    public string Key { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
