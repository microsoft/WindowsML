// Copyright (C) Microsoft Corporation. All rights reserved.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TaskStudio;

public sealed class StudioMessage : INotifyPropertyChanged
{
    private string text;
    private string detail = "";

    public StudioMessage(bool isUser, string text)
    {
        this.text = text;
        Role = isUser ? "You" : "Windows ML";
        Glyph = isUser ? "\uE77B" : "\uE945";
        Alignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        Background = Brush(isUser ? 0x1B2C43u : 0x151B23u);
        BorderBrush = Brush(isUser ? 0x2D4869u : 0x28323Eu);
        AvatarBackground = Brush(isUser ? 0x273446u : 0x203C62u);
        AvatarForeground = Brush(isUser ? 0xB9C9DBu : 0x91C2FFu);
    }

    public string Role { get; }
    public string Glyph { get; }
    public HorizontalAlignment Alignment { get; }
    public SolidColorBrush Background { get; }
    public SolidColorBrush BorderBrush { get; }
    public SolidColorBrush AvatarBackground { get; }
    public SolidColorBrush AvatarForeground { get; }

    public string Text
    {
        get => text;
        set { text = value; NotifyChanged(); }
    }

    public string Detail
    {
        get => detail;
        set { detail = value; NotifyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush Brush(uint rgb) =>
        new(ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}
