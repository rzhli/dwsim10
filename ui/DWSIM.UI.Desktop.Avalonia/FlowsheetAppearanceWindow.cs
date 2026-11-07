//    Flowsheet Appearance window (cross-platform interface)
//    Copyright 2026 Daniel Wagner Oliveira de Medeiros
//
//    This file is part of DWSIM.
//
//    DWSIM is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.
//
//    DWSIM is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with DWSIM.  If not, see <http://www.gnu.org/licenses/>.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DWSIM.Drawing.SkiaSharp;
using DWSIM.Drawing.SkiaSharp.Appearance;
using DWSIM.Interfaces;
using DWSIM.UI.Shared.Avalonia;
using SkiaSharp;

namespace DWSIM.UI.Desktop.Avalonia;

/// <summary>
/// Edits the appearance of the flowsheet drawing itself: its background color and the grid. Opened from the
/// context menu of an empty area. Changes show at once and are saved with the flowsheet.
/// </summary>
public sealed class FlowsheetAppearanceWindow : Window
{
    private readonly IFlowsheetOptions _options;
    private readonly Action _redraw;
    private readonly CheckBox _followTheme;
    private readonly ColorPicker _picker;
    private readonly CheckBox _gradient;
    private readonly ColorPicker _picker2;
    private readonly TextBlock _contrastWarning;
    private bool _loading;

    /// <param name="setGrid">Shows or hides the grid, keeping the drawing toolbar in step.</param>
    /// <param name="setSnap">Turns snapping to the grid on or off, keeping the drawing toolbar in step.</param>
    public FlowsheetAppearanceWindow(IFlowsheetOptions options, GraphicsSurface? surface, Action redraw,
                                     Action<bool> setGrid, Action<bool> setSnap)
    {
        _options = options;
        _redraw = redraw;

        Title = "Flowsheet Appearance";
        Width = UiScale.Size(420);
        SizeToContent = SizeToContent.Height;
        MaxHeight = 800;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        IconHelper.ApplyWindowIcon(this);

        var panel = new StackPanel { Spacing = UiScale.Size(4) };

        panel.Children.Add(Header("Background"));

        _followTheme = new CheckBox { Content = "Same as the interface theme" };
        ToolTip.SetTip(_followTheme, "White, or dark grey with the dark theme.");
        panel.Children.Add(_followTheme);

        SKColor current = SKColors.White;
        var hasColor = FlowsheetBackground.TryGetColor(options, ref current);
        var start = hasColor ? current : (GraphicsSurface.DarkCanvas ? GraphicsSurface.DarkBackgroundColor : SKColors.White);
        _picker = new ColorPicker { Width = UiScale.Size(160), IsAlphaVisible = false, Color = ToAvalonia(start) };
        var colorRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, UiScale.Size(2), 0, 0) };
        colorRow.Children.Add(new TextBlock { Text = "Color", VerticalAlignment = VerticalAlignment.Center, MinWidth = UiScale.Size(60), Margin = new Thickness(0, 0, UiScale.Size(8), 0) });
        colorRow.Children.Add(_picker);
        panel.Children.Add(colorRow);

        var presets = new WrapPanel { Margin = new Thickness(UiScale.Size(60), UiScale.Size(2), 0, 0) };
        foreach (var p in FlowsheetBackground.Presets)
        {
            var swatch = new Button
            {
                Width = UiScale.Size(26), Height = UiScale.Size(22), MinWidth = 0, MinHeight = 0,
                Margin = new Thickness(0, 0, UiScale.Size(4), UiScale.Size(4)), Padding = new Thickness(0),
                Background = new SolidColorBrush(ToAvalonia(p.Value)),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(swatch, p.Key);
            var color = p.Value;
            swatch.Click += (_, _) =>
            {
                _followTheme.IsChecked = false;
                _picker.Color = ToAvalonia(color);
                Apply();
            };
            presets.Children.Add(swatch);
        }
        panel.Children.Add(presets);

        // a vertical gradient, fixed to the view, from the color above to this one at the bottom
        SKColor bottom = start;
        var hasGradient = FlowsheetBackground.TryGetGradientColor(options, ref bottom);
        _gradient = new CheckBox { Content = "Gradient to", VerticalAlignment = VerticalAlignment.Center, MinWidth = UiScale.Size(100), Margin = new Thickness(0, 0, UiScale.Size(8), 0) };
        ToolTip.SetTip(_gradient, "Shade the background from the color above, at the top of the view, to this color at the bottom.");
        _picker2 = new ColorPicker { Width = UiScale.Size(160), IsAlphaVisible = false, Color = ToAvalonia(bottom) };
        var gradientRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, UiScale.Size(2), 0, 0) };
        gradientRow.Children.Add(_gradient);
        gradientRow.Children.Add(_picker2);
        panel.Children.Add(gradientRow);

        _contrastWarning = new TextBlock
        {
            Text = "One end is dark and the other light: lines and text will be hard to read on part of the flowsheet.",
            TextWrapping = TextWrapping.Wrap, FontSize = UiScale.Font(11), Foreground = Brushes.OrangeRed, IsVisible = false
        };
        panel.Children.Add(_contrastWarning);

        panel.Children.Add(new TextBlock
        {
            Text = "Lines and text turn light on a dark background and dark on a light one. The background is saved with the flowsheet and goes into the images and PDF exported from it.",
            TextWrapping = TextWrapping.Wrap, FontSize = UiScale.Font(11), Opacity = 0.7, Margin = new Thickness(0, UiScale.Size(2), 0, 0)
        });

        panel.Children.Add(Header("Grid"));
        var grid = new CheckBox { Content = "Show grid", IsChecked = options.FlowsheetDisplayGrid };
        grid.IsCheckedChanged += (_, _) => setGrid(grid.IsChecked == true);
        var snap = new CheckBox { Content = "Snap objects to the grid", IsChecked = options.FlowsheetSnapToGrid };
        snap.IsCheckedChanged += (_, _) => setSnap(snap.IsChecked == true);
        panel.Children.Add(grid);
        panel.Children.Add(snap);

        _loading = true;
        _followTheme.IsChecked = !hasColor;
        _gradient.IsChecked = hasGradient;
        UpdateEnabled();
        _loading = false;
        UpdateWarning();

        _followTheme.IsCheckedChanged += (_, _) => { UpdateEnabled(); Apply(); };
        _gradient.IsCheckedChanged += (_, _) => { UpdateEnabled(); Apply(); };
        _picker.ColorChanged += (_, _) => Apply();
        _picker2.ColorChanged += (_, _) => Apply();

        var close = new Button { Content = "Close", MinWidth = UiScale.Size(90), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, UiScale.Size(10), 0, 0) };
        close.Classes.Add("dialog");
        close.Click += (_, _) => Close();

        // Keep Close reachable when WindowFit constrains a scaled dialog to the screen.
        // The minimum window width stays unscaled so a small display can still fit it.
        var root = new DockPanel { Margin = new Thickness(UiScale.Size(12)) };
        DockPanel.SetDock(close, global::Avalonia.Controls.Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        });
        Content = root;
    }

    private void UpdateEnabled()
    {
        var own = _followTheme.IsChecked != true;
        _picker.IsEnabled = own;
        _gradient.IsEnabled = own;
        _picker2.IsEnabled = own && _gradient.IsChecked == true;
    }

    private void UpdateWarning()
    {
        _contrastWarning.IsVisible = _followTheme.IsChecked != true && _gradient.IsChecked == true &&
                                     FlowsheetBackground.ContrastDiffers(ToSkia(_picker.Color), ToSkia(_picker2.Color));
    }

    private void Apply()
    {
        if (_loading) return;
        if (_followTheme.IsChecked == true)
            FlowsheetBackground.SetColor(_options, null);
        else
        {
            FlowsheetBackground.SetColor(_options, ToSkia(_picker.Color));
            FlowsheetBackground.SetGradientColor(_options, _gradient.IsChecked == true ? ToSkia(_picker2.Color) : null);
        }
        UpdateWarning();
        _redraw();
    }

    private static SKColor ToSkia(Color c) => new(c.R, c.G, c.B);

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontWeight = FontWeight.SemiBold, FontSize = UiScale.Font(12), Margin = new Thickness(0, UiScale.Size(6), 0, UiScale.Size(2))
    };

    private static Color ToAvalonia(SKColor c) => Color.FromRgb(c.Red, c.Green, c.Blue);
}
