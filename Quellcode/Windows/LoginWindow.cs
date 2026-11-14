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

    public DialogResult ShowForAbsence(Form owner, string title, Action shown)
    {
        Text = title;
        ClientSize = LogicalToDeviceUnits(new Size(585, 710));
        var area = Screen.FromControl(owner).WorkingArea;
        var width = Math.Min(Width, area.Width);
        var height = Math.Min(Height, area.Height);
        MinimumSize = new Size(Math.Min(460, width), Math.Min(440, height));
        Bounds = new Rectangle(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
        FitBrowser();
        EventHandler onShown = (_, _) => shown();
        Shown += onShown;
        try { return ShowDialog(owner); }
        finally { Shown -= onShown; }
    }

    public void FocusBrowser() => controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

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
