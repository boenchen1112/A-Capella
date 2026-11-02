using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using A_Capella.App.ViewModels;
using A_Capella.Engine.Presets;

namespace A_Capella.App;

/// <summary>
/// A preset dropdown that opens as FabFilter's own preset menu does: "Off", then one submenu per
/// category (folders inside folders become submenus inside submenus) listing that category's presets.
/// The chosen preset is shown on the button and ticked in the menu.
/// </summary>
public sealed class PresetPicker : Button
{
    public static readonly StyledProperty<IReadOnlyList<PresetChoice>?> ChoicesProperty =
        AvaloniaProperty.Register<PresetPicker, IReadOnlyList<PresetChoice>?>(nameof(Choices));

    public static readonly StyledProperty<PresetChoice?> SelectedChoiceProperty =
        AvaloniaProperty.Register<PresetPicker, PresetChoice?>(nameof(SelectedChoice), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private readonly TextBlock _label = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    // Styled like a plain Button.
    protected override Type StyleKeyOverride => typeof(Button);

    static PresetPicker()
    {
        SelectedChoiceProperty.Changed.AddClassHandler<PresetPicker>((picker, _) =>
        {
            picker.ShowLabel();
            picker.SelectedChoiceChanged?.Invoke(picker, EventArgs.Empty);
        });
    }

    public PresetPicker()
    {
        var arrow = new TextBlock { Text = "▾", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(arrow, Dock.Right);
        Content = new DockPanel { LastChildFill = true, Children = { arrow, _label } };
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(8, 4, 8, 4);
        Click += (_, _) => OpenMenu();
        ShowLabel();
    }

    public IReadOnlyList<PresetChoice>? Choices
    {
        get => GetValue(ChoicesProperty);
        set => SetValue(ChoicesProperty, value);
    }

    public PresetChoice? SelectedChoice
    {
        get => GetValue(SelectedChoiceProperty);
        set => SetValue(SelectedChoiceProperty, value);
    }

    /// <summary>Raised when the selection changes, whether the user picked or the code set it.</summary>
    public event EventHandler? SelectedChoiceChanged;

    private void ShowLabel() => _label.Text = SelectedChoice?.Label ?? "Off";

    private void OpenMenu() => CreateMenu().Open(this);

    /// <summary>The menu the button opens, wired so picking an entry sets SelectedChoice.</summary>
    public ContextMenu CreateMenu()
    {
        var menu = BuildMenu(Choices ?? Array.Empty<PresetChoice>(), SelectedChoice, choice => SelectedChoice = choice);
        menu.Placement = PlacementMode.Bottom;
        menu.MinWidth = Bounds.Width;
        return menu;
    }

    private static MenuItem NewItem(string header, bool isChecked)
    {
        var item = new MenuItem { Header = header };
        if (isChecked)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = true;
        }
        return item;
    }

    /// <summary>The menu for a list of choices: Off first, then categories as nested submenus.</summary>
    public static ContextMenu BuildMenu(IReadOnlyList<PresetChoice> choices, PresetChoice? selected, Action<PresetChoice> pick)
    {
        var menu = new ContextMenu();
        var submenus = new Dictionary<string, MenuItem>();

        MenuItem Submenu(string[] path)
        {
            string key = string.Join("/", path);
            if (submenus.TryGetValue(key, out var existing)) return existing;

            var item = new MenuItem { Header = FabFilterPresetLibrary.Title(path[^1]) };
            if (path.Length == 1) menu.Items.Add(item);
            else Submenu(path[..^1]).Items.Add(item);
            submenus[key] = item;
            return item;
        }

        foreach (var choice in choices)
        {
            bool isSelected = Equals(choice.Ref, selected?.Ref);
            var entry = NewItem(choice.Label, isSelected);
            var picked = choice;
            entry.Click += (_, _) => pick(picked);

            if (choice.Ref is null)
            {
                menu.Items.Add(entry);
                menu.Items.Add(new Separator());
            }
            else if (choice.Ref.Category.Length == 0)
                menu.Items.Add(entry);
            else
            {
                var submenu = Submenu(choice.Ref.Category.Split('/'));
                submenu.Items.Add(entry);
                // Tick the path down to the chosen preset so the open menu shows where it lives.
                if (isSelected)
                    for (var p = choice.Ref.Category.Split('/'); p.Length > 0; p = p[..^1])
                    {
                        var parent = Submenu(p);
                        parent.ToggleType = MenuItemToggleType.CheckBox;
                        parent.IsChecked = true;
                    }
            }
        }
        return menu;
    }
}
