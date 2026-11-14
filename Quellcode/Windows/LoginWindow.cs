using Microsoft.Web.WebView2.Core;

namespace WebUntisAuswertung;

internal sealed class LoginWindow : Form
{
    private readonly Panel viewport = new() { Dock = DockStyle.Fill };
    private readonly Label address = new()
    {
        Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 0, 0, 0),
        TextAlign = ContentAlignment.MiddleLeft, Text = UntisBrowser.Origin
    };
    private CoreWebView2Controller? controller;

    public CoreWebView2 Browser => controller!.CoreWebView2;
    public event Action? Canceled;

    public LoginWindow()
    {
        Text = "Bei WebUntis anmelden";
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ClientSize = new Size(880, 690);
        MinimumSize = new Size(460, 440);
        Controls.Add(viewport);
        Controls.Add(address);
        viewport.Resize += (_, _) => FitBrowser();
        Move += (_, _) => controller?.NotifyParentWindowPositionChanged();
    }

    public async Task InitializeAsync(CoreWebView2Environment environment)
    {
        // The handle and viewport exist before the window is shown. They also host the background report page.
        var parent = viewport.Handle;
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = "WebUntis";
        options.IsInPrivateModeEnabled = true;
        controller = await environment.CreateCoreWebView2ControllerAsync(parent, options);
        FitBrowser();
        controller.IsVisible = true;
    }

    public void ShowForLogin(Form owner)
    {
        if (!Visible)
        {
            var area = Screen.FromControl(owner).WorkingArea;
            var preferred = SizeFromClientSize(new Size(880, 910));
            var width = Math.Min(preferred.Width, area.Width);
            var height = Math.Min(preferred.Height, area.Height);
            MinimumSize = new Size(Math.Min(460, width), Math.Min(440, height));
            var bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
            Bounds = bounds;
            Show(owner);
            if (WindowState == FormWindowState.Normal && !area.Contains(Bounds)) Bounds = bounds;
        }
        FitBrowser();
        Activate();
    }

    public void ShowForExcuse(Form owner, Size content)
    {
        Text = "Entschuldigung · WebUntis";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = Size.Empty;
        address.Hide();
        var ownerArea = Screen.FromControl(owner).WorkingArea;
        // Move the existing WebView to the owner's monitor before converting CSS pixels.
        Location = ownerArea.Location;
        FitExcuseWindow(ownerArea, center: true);
        EventHandler onShown = (_, _) =>
        {
            FitExcuseWindow(ownerArea, center: true);
            controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        };
        EventHandler<object> scaleChanged = (_, _) => FitExcuseWindow(Screen.FromControl(this).WorkingArea, center: false);
        Shown += onShown;
        if (controller != null)
        {
            controller.RasterizationScaleChanged += scaleChanged;
            controller.ZoomFactorChanged += scaleChanged;
        }
        try { ShowDialog(owner); }
        finally
        {
            Shown -= onShown;
            if (controller != null)
            {
                controller.RasterizationScaleChanged -= scaleChanged;
                controller.ZoomFactorChanged -= scaleChanged;
            }
        }

        void FitExcuseWindow(Rectangle area, bool center)
        {
            if (controller == null || IsDisposed) return;
            var scale = controller.RasterizationScale * controller.ZoomFactor;
            var preferred = SizeFromClientSize(new Size((int)Math.Ceiling(content.Width * scale), (int)Math.Ceiling(content.Height * scale)));
            var width = Math.Min(preferred.Width, area.Width);
            var height = Math.Min(preferred.Height, area.Height);
            var x = center ? area.X + (area.Width - width) / 2 : Math.Clamp(Left, area.Left, area.Right - width);
            var y = center ? area.Y + (area.Height - height) / 2 : Math.Clamp(Top, area.Top, area.Bottom - height);
            Bounds = new Rectangle(x, y, width, height);
            FitBrowser();
        }
    }

    public void ShowAddress(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            address.Text = uri.GetLeftPart(UriPartial.Authority);
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        base.OnFormClosing(args);
        if (Modal || args.CloseReason != CloseReason.UserClosing) return;
        args.Cancel = true;
        Hide();
        Canceled?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            controller?.Close();
            controller = null;
        }
        base.Dispose(disposing);
    }

    private void FitBrowser()
    {
        if (controller != null) controller.Bounds = viewport.ClientRectangle;
    }
}
