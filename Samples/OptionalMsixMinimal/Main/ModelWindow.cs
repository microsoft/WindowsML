// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

internal sealed class ModelWindow : Form
{
    private readonly string mainName;
    private readonly string optionalName;
    private readonly string publisher;
    private bool busy;
    private bool removing;
    private readonly TextBox packageUrl = new()
    {
        Dock = DockStyle.Top,
        PlaceholderText = "Trusted catalog JSON: HTTPS URL or absolute local file path",
        AccessibleName = "Model catalog location"
    };
    private readonly Button install = new()
    {
        Text = "Install optional model",
        AutoSize = true,
        Enabled = false
    };
    private readonly Button uninstall = new()
    {
        Text = "Uninstall optional model",
        AutoSize = true,
        Enabled = false
    };
    private readonly Label operationStatus = new()
    {
        AutoSize = true,
        Dock = DockStyle.Top
    };
    private readonly Label status = new()
    {
        AutoSize = true,
        Dock = DockStyle.Top,
        Text = "Checking optional package..."
    };

    private readonly NumericUpDown modelInput = new()
    {
        Minimum = -1000,
        Maximum = 1000,
        DecimalPlaces = 2,
        Value = 4,
        AccessibleName = "Model input x"
    };
    private readonly Button runModel = new()
    {
        Text = "Run model",
        AutoSize = true,
        Enabled = false
    };
    private readonly Label modelPath = new()
    {
        AutoSize = true,
        Dock = DockStyle.Top
    };
    private readonly Label modelResult = new()
    {
        AutoSize = true,
        Dock = DockStyle.Top
    };

    private ModelWindow(string mainName, string optionalName, string publisher)
    {
        this.mainName = mainName;
        this.optionalName = optionalName;
        this.publisher = publisher;

        SuspendLayout();
        AutoScaleDimensions = new System.Drawing.SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Optional MSIX Model";
        ClientSize = new System.Drawing.Size(780, 680);
        MinimumSize = new System.Drawing.Size(660, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.SystemColors.Window;
        ForeColor = System.Drawing.SystemColors.WindowText;

        var refresh = new Button
        {
            Text = "Refresh status",
            AutoSize = true
        };
        refresh.Click += (_, _) => RefreshStatus();

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        var layout = CreateStack();
        layout.Padding = new Padding(24);
        var title = new Label
        {
            Text = "Optional model",
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new System.Drawing.Font(Font.FontFamily, 20, System.Drawing.FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6)
        };
        title.Disposed += (_, _) => title.Font.Dispose();
        layout.Controls.Add(title);
        layout.Controls.Add(new Label
        {
            Text = "Install optional content, then try the demo model: y = 2x + 1.",
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 20)
        });

        var packageSection = AddSection(layout, "Package status");
        packageSection.Controls.Add(status);
        packageSection.Controls.Add(CreateActions(refresh));

        var installSection = AddSection(layout, "1. Install the model");
        installSection.Controls.Add(new Label
        {
            Text = "Catalog JSON URL or local file",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        });
        packageUrl.Margin = new Padding(0, 0, 0, 10);
        installSection.Controls.Add(packageUrl);
        installSection.Controls.Add(CreateActions(install, uninstall));
        installSection.Controls.Add(operationStatus);

        var inferenceSection = AddSection(layout, "2. Run inference");
        var inferenceActions = CreateActions(runModel);
        var inputLabel = new Label
        {
            Text = "Input x",
            AutoSize = true,
            Margin = new Padding(0, 8, 10, 0)
        };
        modelInput.Width = 120;
        modelInput.Margin = new Padding(0, 5, 16, 0);
        inferenceActions.Controls.Add(inputLabel);
        inferenceActions.Controls.Add(modelInput);
        inferenceActions.Controls.SetChildIndex(inputLabel, 0);
        inferenceActions.Controls.SetChildIndex(modelInput, 1);
        inferenceSection.Controls.Add(inferenceActions);
        modelResult.Padding = new Padding(12);
        modelResult.BackColor = System.Drawing.SystemColors.Control;
        inferenceSection.Controls.Add(modelResult);
        inferenceSection.Controls.Add(modelPath);

        foreach (var label in new[] { status, operationStatus, modelResult, modelPath })
        {
            label.Margin = new Padding(0, 4, 0, 8);
            label.UseMnemonic = false;
            var parent = label.Parent!;
            // Bound long package paths and errors to the section width so they wrap.
            parent.SizeChanged += (_, _) =>
                label.MaximumSize = new System.Drawing.Size(
                    Math.Max(1, parent.ClientSize.Width - parent.Padding.Horizontal), 0);
        }
        scroll.Controls.Add(layout);
        Controls.Add(scroll);

        install.Click += async (_, _) => await UseModelAsync(runInference: false);
        runModel.Click += async (_, _) => await UseModelAsync(runInference: true);
        uninstall.Click += async (_, _) => await UninstallOptionalAsync();
        FormClosing += (_, e) =>
        {
            if (busy && !removing && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                operationStatus.Text =
                    "Please wait for the current model operation to finish.";
            }
        };
        Shown += (_, _) => RefreshStatus();
        Activated += (_, _) => RefreshStatus();
        ResumeLayout(true);
    }

    private static TableLayoutPanel CreateStack()
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    private static TableLayoutPanel AddSection(TableLayoutPanel layout, string title)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 24, 16, 12),
            Margin = new Padding(0, 0, 0, 16)
        };
        var content = CreateStack();
        group.Controls.Add(content);
        layout.Controls.Add(group);
        return content;
    }

    private static FlowLayoutPanel CreateActions(params Button[] buttons)
    {
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, 4, 0, 8)
        };
        foreach (var button in buttons)
        {
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Padding = new Padding(12, 5, 12, 5);
            button.Margin = new Padding(0, 0, 10, 0);
            actions.Controls.Add(button);
        }
        return actions;
    }

    private void RefreshStatus()
    {
        if (busy) { return; }
        install.Enabled = false;
        uninstall.Enabled = false;
        runModel.Enabled = false;
        try
        {
            var identity = Package.Current.Id;
            if (identity.Name != mainName || identity.Publisher != publisher)
            {
                throw new InvalidOperationException(
                    "Install Main.msix and launch the installed app.");
            }

            var optional = FindOptional();
            install.Enabled = optional is null;
            uninstall.Enabled = optional is not null;
            var path = optional is null ? null : GetModelPath(optional);
            runModel.Enabled = path is not null && File.Exists(path);
            modelPath.Text = path is null ? "" : $"Optional-package model file:\n{path}";
            if (!runModel.Enabled) { modelResult.Text = ""; }
            status.Text = optional is null
                ? "Optional package: not installed."
                : $"Optional package: installed.\n{optional.Id.FullName}\n" +
                  (runModel.Enabled
                      ? "Model file present."
                      : "Models\\demo.onnx is missing. Replace the old optional package.");
        }
        catch (Exception error) when (
            error is InvalidOperationException or COMException or
                UnauthorizedAccessException or System.IO.IOException)
        {
            status.Text =
                $"Unable to determine status (0x{error.HResult:X8}): {error.Message}";
        }
    }

    private Package? FindOptional() => new PackageManager()
        .FindPackagesForUserWithPackageTypes("", PackageTypes.Optional)
        .SingleOrDefault(package =>
            package.Id.Name == optionalName &&
            package.Id.Publisher == publisher);

    private static string GetModelPath(Package optional) =>
        Path.GetFullPath(Path.Combine(optional.InstalledLocation.Path, "Models", "demo.onnx"));

    private string ResolveModelPath()
    {
        var optional = FindOptional() ?? throw new InvalidOperationException(
            "The expected optional package is not registered.");
        if (!optional.Status.VerifyIsOK())
        {
            throw new InvalidOperationException("The optional package is not healthy.");
        }
        var version = optional.Id.Version;
        if (version.Major != 1 || version.Minor != 0 || version.Build != 0 || version.Revision != 0)
        {
            throw new InvalidOperationException("The installed optional package version does not match the catalog.");
        }
        var path = GetModelPath(optional);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The optional package lacks Models\\demo.onnx. Replace the old package.", path);
        }
        return path;
    }

    private async Task UseModelAsync(bool runInference)
    {
        if (!Uri.TryCreate(packageUrl.Text.Trim(), UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && !url.IsFile) ||
            url.UserInfo.Length != 0)
        {
            operationStatus.Text =
                "Enter an HTTPS catalog JSON URL or absolute local catalog file path.";
            return;
        }

        busy = true;
        install.Enabled = false;
        uninstall.Enabled = false;
        runModel.Enabled = false;
        modelInput.Enabled = false;
        packageUrl.Enabled = false;
        modelResult.Text = "";
        var input = (float)modelInput.Value;
        var operation = "reading the model catalog metadata";
        try
        {
            var identity = Package.Current.Id;
            if (identity.Name != mainName || identity.Publisher != publisher)
            {
                throw new InvalidOperationException(
                    "Install Main.msix and launch the installed app.");
            }
            operationStatus.Text = "Reading model catalog metadata...";
            var familyName = $"{optionalName}_{identity.PublisherId}";
            var package = await DemoCatalog.ReadPackageAsync(url, familyName);
            if (FindOptional() is null)
            {
                operation = "downloading, validating, and installing the optional MSIX";
                operationStatus.Text =
                    "Installing through PackageManager. Package deployment may close this app.";
                await OptionalPackage.InstallAsync(package);
            }
            operation = "locating Models\\demo.onnx in the optional package";
            var path = ResolveModelPath();
            modelPath.Text = $"Optional-package model file:\n{path}";
            if (runInference)
            {
                operation = "running ONNX Runtime inference";
                operationStatus.Text = "Running ONNX Runtime on CPU...";
                var output = await Task.Run(() => DemoModel.Run(path, input));
                modelResult.Text = $"x = {input}; y = {output} (y = 2x + 1)";
                operationStatus.Text = "ORT inference completed.";
            }
            else
            {
                operationStatus.Text = "Optional model installed and ready.";
            }
        }
        catch (OperationCanceledException)
        {
            operationStatus.Text = $"Canceled or timed out while {operation}.";
        }
        catch (Exception error) when (
            error is HttpRequestException or IOException or InvalidDataException or JsonException or
                InvalidOperationException or UnauthorizedAccessException or
                COMException or ArgumentException or NotSupportedException or
                KeyNotFoundException or OnnxRuntimeException)
        {
            operationStatus.Text =
                $"Failed while {operation} (0x{error.HResult:X8}): {error.Message}";
        }
        finally
        {
            busy = false;
            packageUrl.Enabled = true;
            modelInput.Enabled = true;
            RefreshStatus();
        }
    }

    private async Task UninstallOptionalAsync()
    {
        busy = true;
        install.Enabled = false;
        uninstall.Enabled = false;
        runModel.Enabled = false;
        modelInput.Enabled = false;
        packageUrl.Enabled = false;
        try
        {
            var identity = Package.Current.Id;
            if (identity.Name != mainName || identity.Publisher != publisher)
            {
                throw new InvalidOperationException(
                    "Install Main.msix and launch the installed app.");
            }

            var confirmation = MessageBox.Show(
                this,
                "Uninstall the optional model?\n\n" +
                "Windows may close this app during removal. " +
                "The main app remains installed.\n\n" +
                "If the app closes, reopen it to see the updated package status.",
                "Uninstall optional model",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirmation != DialogResult.Yes)
            {
                operationStatus.Text = "Uninstall canceled.";
                return;
            }

            // Read registration after confirmation, not from an earlier UI refresh.
            var optional = FindOptional();
            if (optional is null)
            {
                operationStatus.Text = "The optional package is already absent.";
                return;
            }

            operationStatus.Text =
                "Uninstalling the optional model. Windows may close this app.";
            removing = true;
            await new PackageManager().RemovePackageAsync(optional.Id.FullName);

            if (FindOptional() is not null)
            {
                throw new InvalidOperationException(
                    "The optional package is still registered after removal.");
            }

            modelPath.Text = "";
            modelResult.Text = "";
            operationStatus.Text =
                "Optional model uninstalled. The main app remains installed.";
        }
        catch (OperationCanceledException)
        {
            operationStatus.Text =
                "Removal was canceled. Check the refreshed package status.";
        }
        catch (Exception error) when (
            error is InvalidOperationException or COMException or
                UnauthorizedAccessException or IOException or
                ArgumentException or NotSupportedException)
        {
            operationStatus.Text =
                $"Uninstall failed (0x{error.HResult:X8}): {error.Message}";
        }
        finally
        {
            removing = false;
            busy = false;
            if (!IsDisposed)
            {
                packageUrl.Enabled = true;
                modelInput.Enabled = true;
                RefreshStatus();
            }
        }
    }

    internal static int Run(string mainName, string optionalName, string publisher)
    {
        // WinForms requires STA; the existing async entry point is not STA.
        var thread = new Thread(() =>
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new ModelWindow(mainName, optionalName, publisher));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return 0;
    }

    internal static void AttachParentConsole()
    {
        // Retain console output for commands invoked from a terminal.
        if (!AttachConsole(uint.MaxValue))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED means a console is already attached.
            if (error != 5)
            {
                throw new System.ComponentModel.Win32Exception(
                    error, "Unable to attach to the calling console.");
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
