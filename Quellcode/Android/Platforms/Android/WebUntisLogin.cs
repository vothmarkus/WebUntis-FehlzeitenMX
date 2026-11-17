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

    public static string FormScript(WebUntisLogin? login)
    {
        var origin = JsonSerializer.Serialize(WebUntisReports.Origin);
        var values = login == null ? "null" : JsonSerializer.Serialize(new { login.Username, login.Password }, WebUntisReports.Json);
        return $$"""
            ((origin, login) => {
                if (location.origin !== origin) return false;
                const visible = field => field.getClientRects().length > 0 && !field.closest('[hidden], [inert]');
                const editable = field => visible(field) && !field.disabled && !field.readOnly;
                const isChallenge = input => input.autocomplete === 'one-time-code' ||
                    /(?:otp|totp|mfa|2fa)/i.test(input.name || input.id);
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
                        const submit = [...form.querySelectorAll('button, input[type="submit"]')]
                            .find(button => button.form === form && button.type === 'submit' && visible(button));
                        if (passwords.length !== 1 || names.length !== 1 || !submit ||
                            passwords[0].autocomplete === 'new-password' ||
                            inputs.some(isChallenge)) continue;
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
                    if (matches.length !== previousCount || !/^\/WebUntis\/?$/i.test(address.pathname) ||
                        !/^#\/basic\/login(?:[/?]|$)/i.test(address.hash)) continue;
                    // The current sign-in screen can handle login through a React button instead of a form.
                    const inputs = [...doc.querySelectorAll('input')].filter(editable);
                    const username = inputs.find(field => field.matches('input.un-input-group__input:is([type="text"],[type="email"])'));
                    const password = inputs.find(field => field.matches('input.un-input-group__input[type="password"]'));
                    const buttons = [...doc.querySelectorAll('button.redesigned-button__primary')].filter(button =>
                        visible(button) && (!button.form || button.type === 'button') &&
                        /^(?:login|anmelden)$/i.test(button.textContent.trim()));
                    if (inputs.length === 2 && username && password && password.autocomplete !== 'new-password' &&
                        !inputs.some(isChallenge) && buttons.length === 1)
                        matches.push({ page, username, password, submit: buttons[0], method: 'click' });
                }
                if (matches.length !== 1) return false;
                const { page, form, username, password, submit, method } = matches[0];
                // Preserve fields which the teacher, the browser or the identity provider has already filled.
                if (password.value) return false;
                if (!login) return true;
                if (username.value && username.value !== login.username) return false;
                const setValue = Object.getOwnPropertyDescriptor(page.HTMLInputElement.prototype, 'value').set;
                const fill = (field, value) => {
                    setValue.call(field, value);
                    field.dispatchEvent(new page.Event('input', { bubbles: true }));
                    field.dispatchEvent(new page.Event('change', { bubbles: true }));
                };
                const previousUser = username.value;
                fill(username, login.username);
                fill(password, login.password);
                if (!submit.disabled && method === 'click') {
                    submit.click();
                    return true;
                }
                if (!submit.disabled && method === 'post' && form.checkValidity() && page.HTMLFormElement.prototype.requestSubmit) {
                    page.HTMLFormElement.prototype.requestSubmit.call(form, submit);
                    return true;
                }
                // A React form handles and cancels this event. A native submission would default to GET.
                const Submit = page.SubmitEvent || page.Event;
                if (!method && !submit.disabled && !form.dispatchEvent(new Submit('submit', {
                    bubbles: true, cancelable: true, submitter: submit
                }))) return true;
                fill(password, '');
                fill(username, previousUser);
                return false;
            })({{origin}}, {{values}})
            """;
    }
}
