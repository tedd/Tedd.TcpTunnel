using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tedd.TcpTunnel.Management;

namespace Tedd.TcpTunnel.ControlPanel;

/// <summary>Uses the shared options model so every supported field is editable, including nested and future options.</summary>
public sealed class OptionForm : VerticalStackLayout
{
    private readonly List<Action> _apply = [];
    public OptionForm(object options)
    {
        Spacing = 14;
        Build(options, this, "");
    }

    public void Apply()
    {
        foreach (var apply in _apply) apply();
    }

    private void Build(object value, VerticalStackLayout parent, string prefix)
    {
        foreach (var property in value.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var type = property.PropertyType;
            var current = property.GetValue(value);
            var name = prefix + property.Name;
            if (type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type))
            {
                current ??= Activator.CreateInstance(type)!;
                property.SetValue(value, current);
                var section = new VerticalStackLayout { Spacing = 12 };
                section.Add(new Label { Text = Words(property.Name).ToUpperInvariant(), FontSize = 11, CharacterSpacing = 1, TextColor = Theme("Accent") });
                Build(current, section, name + ".");
                parent.Add(new Border { Content = section, Style = (Style)Application.Current!.Resources["Card"] });
                continue;
            }
            var row = new Grid { ColumnDefinitions = [new(new GridLength(260)), new(GridLength.Star)], ColumnSpacing = 18 };
            var label = new Label { Text = Words(property.Name), VerticalOptions = LayoutOptions.Center };
            row.Add(label);
            View input;
            if (type == typeof(bool))
            {
                var control = new Switch { IsToggled = current is true, HorizontalOptions = LayoutOptions.Start };
                input = control;
                _apply.Add(() => property.SetValue(value, control.IsToggled));
            }
            else if (type.IsEnum && type.IsDefined(typeof(FlagsAttribute)))
            {
                var control = new Entry { Text = current?.ToString() };
                input = control;
                label.Text += " (comma separated)";
                _apply.Add(() =>
                {
                    try { property.SetValue(value, Enum.Parse(type, control.Text ?? "", true)); }
                    catch (ArgumentException) { throw new ArgumentException(name + ": use comma-separated values from " + string.Join(", ", Enum.GetNames(type)) + "."); }
                });
            }
            else if (type.IsEnum)
            {
                var control = new Picker { ItemsSource = Enum.GetNames(type), SelectedItem = current?.ToString() };
                input = control;
                _apply.Add(() => property.SetValue(value, Enum.Parse(type, control.SelectedItem as string ?? throw new ArgumentException(name + ": select a value."))));
            }
            else if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                var control = new Editor { Text = JsonSerializer.Serialize(current, type, ConfigurationFile.Json),
                    HeightRequest = 100, FontFamily = "Consolas", AutoSize = EditorAutoSizeOption.TextChanges };
                input = control;
                _apply.Add(() =>
                {
                    try { property.SetValue(value, JsonSerializer.Deserialize(control.Text ?? "null", type, ConfigurationFile.Json)); }
                    catch (JsonException ex) { throw new ArgumentException(name + ": " + ex.Message); }
                });
                label.Text += typeof(IDictionary).IsAssignableFrom(type) ? " (JSON object)" : " (JSON array)";
            }
            else
            {
                var control = new Entry { Text = Convert.ToString(current, CultureInfo.InvariantCulture), IsPassword = property.Name is "Key" or "CertificatePassword" };
                input = control;
                _apply.Add(() =>
                {
                    try
                    {
                        property.SetValue(value, type == typeof(string) ? (string.IsNullOrEmpty(control.Text) ? null : control.Text) :
                            Convert.ChangeType(control.Text, type, CultureInfo.InvariantCulture));
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException) { throw new ArgumentException(name + ": enter a valid " + type.Name + "."); }
                });
            }
            SemanticProperties.SetDescription(input, name);
            row.Add(input, 1);
            parent.Add(row);
        }
    }

    internal static Color Theme(string name) => (Color)Application.Current!.Resources[name];
    private static string Words(string name) => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1");
}
