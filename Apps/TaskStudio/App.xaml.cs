// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.UI.Xaml;

namespace TaskStudio;

public partial class App : Application
{
    private Window? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
