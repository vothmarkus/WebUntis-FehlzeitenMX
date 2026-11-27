using System.Text.Json;
using System.Text.Json.Serialization;
using WebUntisAuswertung.Core;

namespace WebUntisAuswertung.Mobile;

internal sealed record WebUntisLogin(string Username, string Password, DateTimeOffset? LastAttemptAt = null)
{
    public const string Address = WebUntisReports.Origin + "/WebUntis/?school=bkt";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    [JsonIgnore]
    public bool IsValid => Username is { Length: > 0 and <= 512 } &&
        !string.IsNullOrWhiteSpace(Username) && Password is { Length: > 0 and <= 4096 };

    [JsonIgnore]
    public bool CanRetry => LastAttemptAt is not { } last || DateTimeOffset.UtcNow - last >= RetryDelay;

    public override string ToString() => nameof(WebUntisLogin);

    public static string FormScript(string step, WebUntisLogin? login = null, string? attempt = null)
    {
        var origin = JsonSerializer.Serialize(WebUntisReports.Origin);
        var values = login == null ? "null" : JsonSerializer.Serialize(new { login.Username, login.Password }, WebUntisReports.Json);
        return $$"""
            ((origin, step, login, attempt) => {
                if (location.origin !== origin) return false;
                const visible = field => field.getClientRects().length > 0 && !field.closest('[hidden], [inert]') &&
                    field.ownerDocument.defaultView.getComputedStyle(field).visibility === 'visible';
                const editable = field => visible(field) && !field.disabled && !field.readOnly;
                const isChallenge = input => input.autocomplete === 'one-time-code' ||
                    /(?:otp|totp|mfa|2fa)/i.test(input.name || input.id);
                const label = field => [field.getAttribute('aria-label'), field.getAttribute('placeholder'),
                    ...[...(field.labels || [])].map(node => node.textContent)].filter(Boolean).join(' ').trim();
                const isLogin = button => /^(?:login|anmelden|einloggen|sign in|log in)$/i.test(
                    (button.getAttribute('aria-label') || button.textContent || button.value || '').trim());
                const windows = [window, ...Array.from(document.querySelectorAll(
                    'iframe#embedded-webuntis,iframe[src*="/embedded.do"]'), frame => frame.contentWindow)];
                const matches = [];
                for (const page of windows) {
                    let doc;
                    try {
                        if (page?.location.origin !== origin) continue;
                        doc = page.document;
                    } catch { continue; }
                    const address = new URL(doc.URL);
                    const schools = address.searchParams.getAll('school');
                    if (schools.some(school => school.toLowerCase() !== 'bkt')) continue;
                    const previousCount = matches.length;
                    for (const form of doc.forms) {
                        const inputs = [...form.querySelectorAll('input')].filter(input => input.form === form && editable(input));
                        const passwords = inputs.filter(input => input.type === 'password');
                        const names = inputs.filter(input => (input.type === 'text' || input.type === 'email') &&
                            !/^(?:school|schoolname|schoolsearch)$/i.test(input.name || input.id));
                        const buttons = [...form.querySelectorAll('button, input[type="submit"]')]
                            .filter(button => button.form === form && button.type === 'submit' && visible(button) && isLogin(button));
                        if (passwords.length !== 1 || names.length !== 1 || buttons.length !== 1 ||
                            passwords[0].autocomplete === 'new-password' ||
                            inputs.some(isChallenge)) continue;
                        const [submit] = buttons;
                        let action;
                        try {
                            action = new URL(submit.getAttribute('formaction') || form.getAttribute('action') || doc.URL,
                                doc.baseURI);
                        } catch { continue; }
                        const method = (submit.getAttribute('formmethod') || form.getAttribute('method') || '').toLowerCase();
                        const target = submit.getAttribute('formtarget') || form.getAttribute('target');
                        if (action.origin !== origin || action.username || action.password ||
                            (method && method !== 'post') || (target && target !== '_self')) continue;
                        matches.push({ page, form, username: names[0], password: passwords[0], submit, method });
                    }
                    if (matches.length !== previousCount) continue;
                    const loginRoute = /^\/WebUntis\/?$/i.test(address.pathname) && /^#\/basic\/login(?:[/?]|$)/i.test(address.hash);
                    const gatedRoute = /^\/(?:class-register-reports|absences|absence-times)\/?$/i.test(address.pathname);
                    if (!loginRoute && !gatedRoute) continue;
                    // The sign-in view can also be mounted directly at the protected URL, with no native form.
                    const inputs = [...doc.querySelectorAll('input')].filter(editable);
                    const username = inputs.find(field => ['text', 'email'].includes(field.type) &&
                        (field.matches('.un-input-group__input') || /benutzername|user\s*name/i.test(label(field))));
                    const password = inputs.find(field => field.type === 'password' &&
                        (field.matches('.un-input-group__input') || /passwort|password/i.test(label(field))));
                    const buttons = [...doc.querySelectorAll('button')].filter(button =>
                        visible(button) && !button.form && isLogin(button));
                    if (inputs.length === 2 && username && password && password.autocomplete !== 'new-password' &&
                        !inputs.some(isChallenge) && buttons.length === 1)
                        matches.push({ page, username, password, submit: buttons[0], method: 'click' });
                }
                if (matches.length !== 1) return false;
                const { page, form, username, password, submit, method } = matches[0];
                if (step === 'probe') return !password.value;
                const setValue = Object.getOwnPropertyDescriptor(page.HTMLInputElement.prototype, 'value').set;
                const fill = (field, value) => {
                    setValue.call(field, value);
                    field.dispatchEvent(new page.Event('input', { bubbles: true }));
                    field.dispatchEvent(new page.Event('change', { bubbles: true }));
                };
                const marker = '__fehlzeitenMxLogin';
                if (step === 'fill') {
                    // Leave credentials entered by the teacher or autofill in place.
                    if (password.value || (username.value && username.value !== login.username)) return false;
                    page[marker] = { attempt, username, password, previousUser: username.value };
                    fill(username, login.username);
                    fill(password, login.password);
                    return true;
                }
                const filled = page[marker];
                if (filled?.attempt !== attempt || filled.username !== username || filled.password !== password) return false;
                const unchanged = username.value === login.username && password.value === login.password;
                if (step === 'clear') {
                    if (unchanged) {
                        fill(password, '');
                        fill(username, filled.previousUser);
                    }
                    delete page[marker];
                    return false;
                }
                // Let the web framework process the field changes before checking the submit button.
                if (!unchanged) return false;
                if (submit.disabled || submit.getAttribute('aria-disabled') === 'true') return null;
                if (method === 'click') {
                    submit.click();
                    delete page[marker];
                    return true;
                }
                if (method === 'post' && form.checkValidity() && page.HTMLFormElement.prototype.requestSubmit) {
                    page.HTMLFormElement.prototype.requestSubmit.call(form, submit);
                    delete page[marker];
                    return true;
                }
                // A React form handles and cancels this event. A native submission would default to GET.
                const Submit = page.SubmitEvent || page.Event;
                if (!method && !form.dispatchEvent(new Submit('submit', {
                    bubbles: true, cancelable: true, submitter: submit
                }))) {
                    delete page[marker];
                    return true;
                }
                return false;
            })({{origin}}, {{JsonSerializer.Serialize(step)}}, {{values}}, {{JsonSerializer.Serialize(attempt)}})
            """;
    }
}
