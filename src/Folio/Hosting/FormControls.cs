using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Folio.Dom;
using Folio.Interaction;

namespace Folio;

/// <summary>The constraint validation states of a form control (https://html.spec.whatwg.org/multipage/form-control-infrastructure.html#validity-states).</summary>
[Flags]
internal enum ValidityFlags
{
    None = 0,
    ValueMissing = 1,
    TypeMismatch = 2,
    PatternMismatch = 4,
    TooLong = 8,
    TooShort = 16,
    RangeUnderflow = 32,
    RangeOverflow = 64,
    StepMismatch = 128,
    BadInput = 256,
    CustomError = 512,
}

/// <summary>
/// What forms and their controls do, for scripts and for trusted input alike: values and selections, constraint validation, the
/// entry list, and the activation behaviour of buttons and checkable inputs, with the events each fires. Attached to a
/// <see cref="Document"/> that draws and takes input, or detached on a bare document tree (scripts only, nothing to repaint).
/// </summary>
internal sealed class FormControls
{
    private static readonly ConditionalWeakTable<DocumentNode, FormControls> Detached = new();

    private readonly DocumentNode _node;
    private readonly Document? _host;
    private readonly Dictionary<ElementNode, TextControlState> _states = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<ElementNode, StrongBox<string>> _custom = new();

    internal FormControls(DocumentNode node, Document? host)
    {
        _node = node;
        _host = host;
    }

    /// <summary>The form logic of a document tree that has no <see cref="Document"/> around it.</summary>
    internal static FormControls For(DocumentNode node) => Detached.GetValue(node, n => new FormControls(n, null));

    private static bool IsHtml(ElementNode e, string name) => e.Name.Namespace == Namespaces.Html && e.LocalName == name;

    private static readonly HashSet<string> InputTypes =
    [
        "text", "search", "url", "tel", "email", "password", "date", "month", "week", "time", "datetime-local", "number", "range", "color",
        "checkbox", "radio", "file", "submit", "image", "reset", "button", "hidden",
    ];

    private static string InputType(ElementNode e)
    {
        var type = (e.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
        return InputTypes.Contains(type) ? type : "text";
    }

    // ---- events ----

    /// <summary>Dispatches a trusted event to the document's listeners; false when one of them cancelled it.</summary>
    internal bool Fire(Node target, string type, bool bubbles, bool cancelable, Node? related = null)
    {
        if (_node.EventDispatcher is not { } dispatcher)
            return true;
        return dispatcher(target, type, new EventDispatchArgs { Bubbles = bubbles, Cancelable = cancelable, Composed = false, RelatedTarget = related });
    }

    private void Changed(ElementNode element)
    {
        if (_host is null)
            return;
        _host.Update();
    }

    // ---- values and selection ----

    private static bool HasTextState(ElementNode e) =>
        IsHtml(e, "textarea") || (IsHtml(e, "input") && InputType(e) is "text" or "search" or "url" or "tel" or "email" or "password" or "number");

    internal TextControlState? TextState(ElementNode element)
    {
        if (_host is not null)
            return _host.GetOrCreateTextControlState(element);
        if (!HasTextState(element))
            return null;
        if (!_states.TryGetValue(element, out var state))
            _states[element] = state = new TextControlState(element);
        return state;
    }

    internal string Value(ElementNode element)
    {
        if (_host is not null)
            return _host.GetControlValue(element);
        if (_states.TryGetValue(element, out var state))
            return state.Value;
        return element.Value ?? element.GetAttribute("value") ?? (IsHtml(element, "textarea") ? element.TextContent : null) ?? "";
    }

    /// <summary>Sets a text control's value as the script's <c>value</c> setter does: no events, the caret to the end.</summary>
    internal void SetValue(ElementNode element, string value)
    {
        var changed = TextState(element) is { } existing && existing.Value != value;
        if (_host is not null)
            _host.SetControlValue(element, value);
        else if (TextState(element) is { } state)
        {
            state.Value = value;
            element.Value = state.Value;
            element.DisplayValue = state.DisplayText;
        }
        else
            element.Value = value;
        // A new value puts the caret at its end.
        if (changed && TextState(element) is { } edited)
            SetSelectionRange(element, edited.Value.Length, edited.Value.Length);
    }

    /// <summary>Whether the control has a selection (https://html.spec.whatwg.org/multipage/input.html#concept-input-apply): text, search, url, tel and password inputs and textareas.</summary>
    internal static bool SupportsSelection(ElementNode e) =>
        IsHtml(e, "textarea") || (IsHtml(e, "input") && InputType(e) is "text" or "search" or "url" or "tel" or "password");

    internal int SelectionStart(ElementNode e) => TextState(e)?.SelectionStart ?? 0;

    internal int SelectionEnd(ElementNode e) => TextState(e)?.SelectionEnd ?? 0;

    internal string SelectionDirection(ElementNode e) => TextState(e) is { HasSelection: true, SelectionIsBackwards: true } ? "backward" : TextState(e) is { HasSelection: true } ? "forward" : "none";

    internal void SetSelectionRange(ElementNode e, int start, int end, string? direction = null)
    {
        if (TextState(e) is not { } state)
            return;
        state.SetSelectionRange(start, end, backwards: direction == "backward");
        ShowSelection(e, state);
    }

    // Setting one end sets the whole range (the spec's "set the selection range"): the other end follows when they would cross.
    internal void SetSelectionStart(ElementNode e, int value)
    {
        if (TextState(e) is not { } state)
            return;
        SetSelectionRange(e, value, Math.Max(value, state.SelectionEnd), SelectionDirection(e));
    }

    internal void SetSelectionEnd(ElementNode e, int value)
    {
        if (TextState(e) is not { } state)
            return;
        SetSelectionRange(e, Math.Min(state.SelectionStart, value), value, SelectionDirection(e));
    }

    internal void SelectAll(ElementNode e)
    {
        if (TextState(e) is not { } state)
            return;
        state.SelectAll();
        ShowSelection(e, state);
    }

    private void ShowSelection(ElementNode e, TextControlState state)
    {
        if (_host is not null)
            _host.UpdateControlSelection(e, state);
    }

    /// <summary>Replaces a range of a text control's value (<c>setRangeText</c>); <paramref name="mode"/> says where the selection goes after.</summary>
    internal void SetRangeText(ElementNode e, string replacement, int start, int end, string mode)
    {
        if (TextState(e) is not { } state)
            return;
        var value = state.Value;
        end = Math.Min(end, value.Length);
        start = Math.Min(start, end);
        var (oldStart, oldEnd) = (state.SelectionStart, state.SelectionEnd);
        SetValue(e, value[..start] + replacement + value[end..]);
        var delta = replacement.Length - (end - start);
        var newEnd = start + replacement.Length;
        int selectStart, selectEnd;
        switch (mode)
        {
            case "select":
                (selectStart, selectEnd) = (start, newEnd);
                break;
            case "start":
                (selectStart, selectEnd) = (start, start);
                break;
            case "end":
                (selectStart, selectEnd) = (newEnd, newEnd);
                break;
            default:
                // "preserve": a selection edge after the range moves with the text; one inside it goes to its edge.
                int Move(int p) => p > end ? p + delta : p > start ? (p <= newEnd ? p : newEnd) : p;
                (selectStart, selectEnd) = (Move(oldStart), Move(oldEnd));
                break;
        }
        SetSelectionRange(e, selectStart, selectEnd);
    }

    // ---- owners and lists ----

    /// <summary>The form a control belongs to: the one its form attribute names, else the nearest ancestor form.</summary>
    internal ElementNode? FormOwner(ElementNode control)
    {
        if (control.GetAttribute("form") is { } id)
            return _node.Descendants.OfType<ElementNode>().FirstOrDefault(e => IsHtml(e, "form") && e.GetAttribute("id") == id);
        for (var cursor = control.Parent as ElementNode; cursor is not null; cursor = cursor.Parent as ElementNode)
        {
            if (IsHtml(cursor, "form"))
                return cursor;
        }
        return null;
    }

    private static bool IsListed(ElementNode e) =>
        e.Name.Namespace == Namespaces.Html && e.LocalName is "button" or "fieldset" or "input" or "object" or "output" or "select" or "textarea";

    /// <summary>The listed elements of a form, in tree order (https://html.spec.whatwg.org/multipage/forms.html#category-listed), without image inputs.</summary>
    internal List<ElementNode> Elements(ElementNode form) =>
        _node.Descendants.OfType<ElementNode>().Where(e => IsListed(e) && FormOwner(e) == form && !(IsHtml(e, "input") && InputType(e) == "image")).ToList();

    private static bool IsSubmitButton(ElementNode e) =>
        (IsHtml(e, "button") && (e.GetAttribute("type") ?? "submit").ToLowerInvariant() is not ("button" or "reset"))
        || (IsHtml(e, "input") && InputType(e) is "submit" or "image");

    internal static bool IsSubmitter(ElementNode e) => IsSubmitButton(e);

    // ---- constraint validation ----

    private bool IsBarred(ElementNode e)
    {
        if (ElementNode.IsDisabledControl(e))
            return true;
        for (var cursor = e.Parent as ElementNode; cursor is not null; cursor = cursor.Parent as ElementNode)
        {
            if (IsHtml(cursor, "datalist"))
                return true;
        }
        if (IsHtml(e, "input"))
            return InputType(e) is "hidden" or "reset" or "button" || (e.GetAttribute("readonly") is not null && InputType(e) is not ("checkbox" or "radio" or "file" or "range" or "color"));
        if (IsHtml(e, "button"))
            return (e.GetAttribute("type") ?? "submit").ToLowerInvariant() is "reset" or "button";
        if (IsHtml(e, "textarea"))
            return e.GetAttribute("readonly") is not null;
        return false;
    }

    /// <summary>Whether the control takes part in constraint validation.</summary>
    internal bool WillValidate(ElementNode e) =>
        e.Name.Namespace == Namespaces.Html && e.LocalName is "button" or "input" or "select" or "textarea" && !IsBarred(e);

    internal ValidityFlags Validity(ElementNode e)
    {
        var flags = ValidityFlags.None;
        if (_custom.TryGetValue(e, out var message) && message.Value is { Length: > 0 })
            flags |= ValidityFlags.CustomError;
        if (!WillValidate(e))
            return flags & ValidityFlags.CustomError;
        var required = e.GetAttribute("required") is not null;
        if (IsHtml(e, "select"))
        {
            if (required && !SelectHasValue(e))
                flags |= ValidityFlags.ValueMissing;
            return flags;
        }
        if (IsHtml(e, "button"))
            return flags;
        var value = Value(e);
        var type = IsHtml(e, "textarea") ? "textarea" : InputType(e);
        switch (type)
        {
            case "checkbox":
                if (required && !e.IsChecked)
                    flags |= ValidityFlags.ValueMissing;
                return flags;
            case "radio":
                if (required && !RadioGroup(e).Any(r => r.IsChecked))
                    flags |= ValidityFlags.ValueMissing;
                return flags;
            case "file":
                return flags;
        }
        if (required && value.Length == 0)
            flags |= ValidityFlags.ValueMissing;
        if (value.Length == 0)
            return flags;
        if (type == "email" && !EmailMatches(e, value))
            flags |= ValidityFlags.TypeMismatch;
        else if (type == "url" && !(Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme.Length > 0 && value.IndexOf(':') > 0))
            flags |= ValidityFlags.TypeMismatch;
        if (type is "text" or "search" or "url" or "tel" or "email" or "password" && e.GetAttribute("pattern") is { Length: > 0 } pattern && !PatternMatches(pattern, value, e.GetAttribute("multiple") is not null && type == "email"))
            flags |= ValidityFlags.PatternMismatch;
        if (type == "number")
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                return flags;
            if (double.TryParse(e.GetAttribute("min"), NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && number < min)
                flags |= ValidityFlags.RangeUnderflow;
            if (double.TryParse(e.GetAttribute("max"), NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && number > max)
                flags |= ValidityFlags.RangeOverflow;
            var stepText = e.GetAttribute("step");
            if (!string.Equals(stepText?.Trim(), "any", StringComparison.OrdinalIgnoreCase))
            {
                var step = double.TryParse(stepText, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;
                var baseValue = double.TryParse(e.GetAttribute("min"), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : double.TryParse(e.GetAttribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                var steps = (number - baseValue) / step;
                if (Math.Abs(steps - Math.Round(steps)) > 1e-9)
                    flags |= ValidityFlags.StepMismatch;
            }
        }
        return flags;
    }

    private static bool PatternMatches(string pattern, string value, bool multiple)
    {
        try
        {
            var regex = new Regex("^(?:" + pattern + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            return multiple ? value.Split(',').All(part => regex.IsMatch(part.Trim())) : regex.IsMatch(value);
        }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
        {
            return true; // a pattern that is not a regular expression is ignored
        }
    }

    // https://html.spec.whatwg.org/multipage/input.html#valid-e-mail-address
    private static readonly Regex Email = new(
        @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static bool EmailMatches(ElementNode e, string value) =>
        e.GetAttribute("multiple") is not null
            ? value.Split(',').Select(part => part.Trim()).All(part => Email.IsMatch(part))
            : Email.IsMatch(value.Trim());

    private bool SelectHasValue(ElementNode select)
    {
        var options = select.Descendants.OfType<ElementNode>().Where(o => IsHtml(o, "option")).ToList();
        var selected = options.Where(o => o.IsSelected).ToList();
        if (selected.Count == 0 && select.GetAttribute("multiple") is null && options.Count > 0)
            selected.Add(options.FirstOrDefault(o => !ElementNode.IsDisabledControl(o)) ?? options[0]);
        // A placeholder label option (the first, with an empty value) does not count as a value.
        if (select.GetAttribute("multiple") is null && select.GetAttribute("size") is null or "1")
            return selected.Any(o => !(o == options[0] && (o.Value ?? o.GetAttribute("value") ?? o.TextContent ?? "").Length == 0 && o.Parent == select));
        return selected.Count > 0;
    }

    internal List<ElementNode> RadioGroup(ElementNode radio)
    {
        var name = radio.GetAttribute("name");
        var form = FormOwner(radio);
        if (string.IsNullOrEmpty(name))
            return [radio];
        return _node.Descendants.OfType<ElementNode>().Where(e => IsHtml(e, "input") && InputType(e) == "radio" && e.GetAttribute("name") == name && FormOwner(e) == form).ToList();
    }

    internal string ValidationMessage(ElementNode e)
    {
        if (!WillValidate(e))
            return "";
        var flags = Validity(e);
        if (_custom.TryGetValue(e, out var custom) && flags.HasFlag(ValidityFlags.CustomError))
            return custom.Value ?? "";
        var type = IsHtml(e, "input") ? InputType(e) : "";
        if (flags.HasFlag(ValidityFlags.ValueMissing))
            return IsHtml(e, "select") ? "Please select an item in the list." : type == "checkbox" ? "Please check this box if you want to proceed." : type == "radio" ? "Please select one of these options." : "Please fill out this field.";
        if (flags.HasFlag(ValidityFlags.TypeMismatch))
            return type == "email" ? "Please include an '@' in the email address." : "Please enter a URL.";
        if (flags.HasFlag(ValidityFlags.PatternMismatch))
            return e.GetAttribute("title") is { Length: > 0 } title ? "Please match the requested format: " + title : "Please match the requested format.";
        if (flags.HasFlag(ValidityFlags.RangeUnderflow))
            return "Value must be greater than or equal to " + e.GetAttribute("min") + ".";
        if (flags.HasFlag(ValidityFlags.RangeOverflow))
            return "Value must be less than or equal to " + e.GetAttribute("max") + ".";
        if (flags.HasFlag(ValidityFlags.StepMismatch))
            return "Please enter a valid value.";
        return "";
    }

    internal void SetCustomValidity(ElementNode e, string message)
    {
        _custom.Remove(e);
        if (message.Length > 0)
            _custom.Add(e, new StrongBox<string>(message));
    }

    /// <summary>Whether the control satisfies its constraints; when not, with <paramref name="fire"/>, an <c>invalid</c> event (cancelable, not bubbling) says so.</summary>
    internal bool CheckValidity(ElementNode control, bool fire)
    {
        if (!WillValidate(control) || Validity(control) == ValidityFlags.None)
            return true;
        if (fire)
            Fire(control, "invalid", bubbles: false, cancelable: true);
        return false;
    }

    /// <summary>Checks every control of a form, firing <c>invalid</c> at each that fails; with <paramref name="report"/> the first is focused, as a browser's validation bubble would draw attention to it.</summary>
    internal bool CheckForm(ElementNode form, bool report)
    {
        var invalid = Elements(form).Where(c => !CheckValidity(c, fire: true)).ToList();
        if (invalid.Count > 0 && report && _host is not null && invalid.First(c => WillValidate(c)) is { } first)
            _host.Input.Focus(first, visible: true);
        return invalid.Count == 0;
    }

    // ---- the entry list and submission ----

    /// <summary>The name and value pairs of a form's submittable controls (https://html.spec.whatwg.org/multipage/form-control-infrastructure.html#constructing-the-form-data-set).</summary>
    internal List<KeyValuePair<string, string>> EntryList(ElementNode form, ElementNode? submitter)
    {
        var data = new List<KeyValuePair<string, string>>();
        foreach (var control in Elements(form))
        {
            if (ElementNode.IsDisabledControl(control) || IsHtml(control, "fieldset") || IsHtml(control, "output") || IsHtml(control, "object"))
                continue;
            for (var cursor = control.Parent as ElementNode; cursor is not null; cursor = cursor.Parent as ElementNode)
            {
                if (IsHtml(cursor, "datalist"))
                    goto skip;
            }
            var name = control.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                continue;
            if (IsHtml(control, "input"))
            {
                var type = InputType(control);
                if (type is "submit" or "button" or "reset" or "image")
                {
                    if (control == submitter && type == "submit")
                        data.Add(new(name, control.Value ?? control.GetAttribute("value") ?? "Submit"));
                }
                else if (type is "checkbox" or "radio")
                {
                    if (control.IsChecked)
                        data.Add(new(name, control.Value ?? control.GetAttribute("value") ?? "on"));
                }
                else if (type != "file")
                    data.Add(new(name, Value(control)));
            }
            else if (IsHtml(control, "textarea"))
                data.Add(new(name, Value(control)));
            else if (IsHtml(control, "select"))
            {
                var options = control.Descendants.OfType<ElementNode>().Where(o => IsHtml(o, "option") && !ElementNode.IsDisabledControl(o)).ToList();
                var selected = options.Where(o => o.IsSelected).ToList();
                if (selected.Count == 0 && options.Count > 0 && control.GetAttribute("multiple") is null && (control.GetAttribute("size") is null or "1"))
                    selected.Add(options[0]);
                foreach (var option in selected)
                    data.Add(new(name, option.Value ?? option.GetAttribute("value") ?? option.TextContent ?? ""));
            }
            else if (IsHtml(control, "button"))
            {
                if (control == submitter)
                    data.Add(new(name, control.Value ?? control.GetAttribute("value") ?? ""));
            }
        skip:;
        }
        return data;
    }

    /// <summary>Submits a form with no validation and no <c>submit</c> event: <c>form.submit()</c>, and the end of <see cref="RequestSubmit"/>.</summary>
    internal void SubmitNow(ElementNode form, ElementNode? submitter) => _host?.SubmitForm(form, submitter);

    /// <summary>
    /// Submits as a user does (<c>form.requestSubmit()</c>, a click on a submit button): the controls are validated unless the form or
    /// the submitter says not to, <c>invalid</c> events say what failed, and a <c>submit</c> event that is not cancelled lets it go.
    /// </summary>
    /// <returns>Whether the form was submitted.</returns>
    internal bool RequestSubmit(ElementNode form, ElementNode? submitter)
    {
        var noValidate = form.GetAttribute("novalidate") is not null || submitter?.GetAttribute("formnovalidate") is not null;
        if (!noValidate && !CheckForm(form, report: true))
            return false;
        if (!Fire(form, "submit", bubbles: true, cancelable: true, related: submitter))
            return false;
        SubmitNow(form, submitter);
        return true;
    }

    /// <summary>A reset button's or <c>form.reset()</c>'s work: a cancelable <c>reset</c> event, then every control back to its default.</summary>
    internal void RequestReset(ElementNode form)
    {
        if (!Fire(form, "reset", bubbles: true, cancelable: true))
            return;
        if (_host is not null)
            _host.ResetForm(form);
        else
            ResetDetached(form);
    }

    private void ResetDetached(ElementNode form)
    {
        foreach (var control in Elements(form))
        {
            if (IsHtml(control, "input") && InputType(control) is "checkbox" or "radio")
            {
                control.Checked = control.GetAttribute("checked") is not null;
                control.Indeterminate = false;
            }
            else if (IsHtml(control, "textarea"))
                SetValue(control, control.TextContent ?? "");
            else if (IsHtml(control, "input"))
                SetValue(control, control.GetAttribute("value") ?? "");
            else if (IsHtml(control, "select"))
            {
                foreach (var option in control.Descendants.OfType<ElementNode>().Where(o => IsHtml(o, "option")))
                    option.Selected = option.GetAttribute("selected") is not null;
            }
        }
    }

    // ---- activation ----

    /// <summary>What <c>element.click()</c> does: a synthetic click, and the element's activation behaviour unless a listener cancelled it.</summary>
    internal void Click(ElementNode element)
    {
        if (ElementNode.IsDisabledControl(element))
            return;
        var checkable = IsHtml(element, "input") && InputType(element) is "checkbox" or "radio";
        // A checkbox or radio changes before the click event is dispatched, and changes back if the click is cancelled.
        bool? wasChecked = null;
        List<ElementNode>? group = null;
        List<bool>? groupBefore = null;
        if (checkable)
        {
            wasChecked = element.IsChecked;
            if (InputType(element) == "checkbox")
                element.Checked = !element.IsChecked;
            else
            {
                group = RadioGroup(element);
                groupBefore = group.Select(r => r.IsChecked).ToList();
                foreach (var radio in group)
                    radio.Checked = radio == element;
            }
            if (_host is not null)
                _host.RepaintForChecked(element, group);
        }
        var notCancelled = Fire(element, "click", bubbles: true, cancelable: true);
        if (!notCancelled)
        {
            if (checkable)
            {
                if (group is null)
                    element.Checked = wasChecked;
                else
                    for (var i = 0; i < group.Count; i++)
                        group[i].Checked = groupBefore![i];
                if (_host is not null)
                    _host.RepaintForChecked(element, group);
            }
            return;
        }
        Activate(element, afterToggle: checkable && (InputType(element) == "checkbox" || wasChecked == false));
    }

    /// <summary>The default action of a click that was not cancelled: submit, reset, a checkable input's input and change events, a label's control.</summary>
    internal void Activate(ElementNode element, bool afterToggle)
    {
        if (IsHtml(element, "input") && InputType(element) is "checkbox" or "radio")
        {
            if (afterToggle)
            {
                Fire(element, "input", bubbles: true, cancelable: false);
                Fire(element, "change", bubbles: true, cancelable: false);
            }
            return;
        }
        var submitter = IsSubmitButton(element);
        if (submitter || (IsHtml(element, "button") && (element.GetAttribute("type") ?? "submit").ToLowerInvariant() == "reset")
            || (IsHtml(element, "input") && InputType(element) == "reset"))
        {
            if (FormOwner(element) is not { } form)
                return;
            if (submitter)
                RequestSubmit(form, element);
            else
                RequestReset(form);
            return;
        }
        if (IsHtml(element, "label") && LabeledControl(element) is { } control && control != element)
            Click(control);
    }

    private ElementNode? LabeledControl(ElementNode label)
    {
        if (label.GetAttribute("for") is { } id)
            return _node.Descendants.OfType<ElementNode>().FirstOrDefault(e => e.GetAttribute("id") == id && e.Name.Namespace == Namespaces.Html && e.LocalName is "button" or "input" or "select" or "textarea");
        return label.Descendants.OfType<ElementNode>().FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName is "button" or "input" or "select" or "textarea");
    }
}
