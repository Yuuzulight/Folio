using Folio.Html;
using Folio.Typography;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>Forms and their controls (#466, #467): the selection API, <c>click()</c> and activation, constraint validation, submission, <c>FormData</c>.</summary>
public class FormApiTests
{
    private static string FontsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "fonts")))
                return Path.Combine(dir.FullName, "tests", "fonts");
        }
        throw new DirectoryNotFoundException("tests/fonts");
    }

    private sealed class Page : IDisposable
    {
        public Page(string body, bool fonts = false)
        {
            Document = Document.Parse("<!DOCTYPE html><body>" + body, new FolioOptions
            {
                Scripting = ScriptingPolicy.ContentProcess,
                Fonts = fonts ? new FontSettings { Source = new FontFolderSource(FontsFolder()), GenericFamilies = new[] { "serif", "sans-serif", "monospace", "system-ui" }.ToDictionary(g => g, _ => (IReadOnlyList<string>)["Folio Box"], StringComparer.OrdinalIgnoreCase) } : FontSettings.Default,
            });
            Document.Input.FormSubmitted += (_, e) => Submitted.Add(e);
            Engine = new JintScriptEngine();
            DomBindings.Install(Engine, Document);
            Engine.Evaluate("var log = []; function el(id) { return document.getElementById(id); } function on(id, type, fn) { el(id).addEventListener(type, fn || function (e) { log.push(type + ':' + (e.target.id || e.target.tagName)); }); }");
        }

        public Document Document { get; }
        public JintScriptEngine Engine { get; }
        public List<FormSubmittedEventArgs> Submitted { get; } = [];

        public string Run(string script) => Engine.Evaluate(script).ToString() ?? "";

        public string Log() => Run("log.join(' ')");

        public void Dispose() => Engine.Dispose();
    }

    // ---- values and the selection API ----

    [Fact]
    public void SettingAValueFiresNoEventsAndPutsTheCaretAtTheEnd()
    {
        using var page = new Page("<input id=i value=abc><textarea id=t>xyz</textarea>");
        page.Run("on('i', 'input'); on('i', 'change'); on('t', 'input'); el('i').value = 'hello'; el('t').value = 'world!';");
        Assert.Equal("", page.Log());
        Assert.Equal("hello,5,5", page.Run("var i = el('i'); i.value + ',' + i.selectionStart + ',' + i.selectionEnd"));
        Assert.Equal("world!,6", page.Run("var t = el('t'); t.value + ',' + t.selectionEnd"));
        // After the control has been focused (and so has editing state), the value setter still reaches the real value.
        Assert.Equal("changed", page.Run("i.focus(); i.value = 'changed'; i.value"));
        Assert.Equal("changed", page.Document.Forms.Value(page.Document.Node.Descendants.OfType<Folio.Dom.ElementNode>().First(e => e.GetAttribute("id") == "i")));
    }

    [Fact]
    public void TheSelectionApiReadsAndMovesTheSelection()
    {
        using var page = new Page("<input id=i value=abcdef>");
        Assert.Equal("6,6,none", page.Run("var i = el('i'); i.selectionStart + ',' + i.selectionEnd + ',' + i.selectionDirection"));
        Assert.Equal("1,3,forward", page.Run("i.setSelectionRange(1, 3); i.selectionStart + ',' + i.selectionEnd + ',' + i.selectionDirection"));
        Assert.Equal("2,4,backward", page.Run("i.setSelectionRange(2, 4, 'backward'); i.selectionStart + ',' + i.selectionEnd + ',' + i.selectionDirection"));
        Assert.Equal("0,6,forward", page.Run("i.select(); i.selectionStart + ',' + i.selectionEnd + ',' + i.selectionDirection"));
        Assert.Equal("4,6", page.Run("i.selectionStart = 4; i.selectionStart + ',' + i.selectionEnd"));
        Assert.Equal("1,1", page.Run("i.selectionEnd = 1; i.selectionStart + ',' + i.selectionEnd")); // an end before the start moves the start
        Assert.Equal("6,6", page.Run("i.setSelectionRange(100, 200); i.selectionStart + ',' + i.selectionEnd")); // clamped to the value
        Assert.Equal("3,3", page.Run("i.setSelectionRange(5, 3); i.selectionStart + ',' + i.selectionEnd")); // an end before the start moves the start
        Assert.Equal("TypeError", page.Run("try { i.setSelectionRange(1); 'no' } catch (e) { e.name }"));
        // What the script selected is what the document selects.
        page.Run("i.focus(); i.setSelectionRange(1, 4);");
        Assert.Equal("bcd", page.Document.Input.SelectedText);
    }

    [Fact]
    public void TheSelectionApiMovesTheHighlightAndTheCaretInTheDisplayList()
    {
        using var page = new Page("<input id=i value=abcdef>", fonts: true);
        page.Document.Paint(800, 600, 1, new Folio.Skia.HarfBuzzShaper(), null);
        static string Fills(Document document) =>
            string.Join("|", document.DisplayList!.Items.Where(item => item.Kind == Folio.Painting.DisplayItemKind.Fill).Select(item => $"{item.Shape.Rect.X:0.##},{item.Shape.Rect.Width:0.##}"));
        page.Run("el('i').focus(); el('i').setSelectionRange(1, 4);");
        var highlighted = Fills(page.Document);
        page.Run("el('i').setSelectionRange(2, 2);");
        var caret = Fills(page.Document);
        Assert.NotEqual(highlighted, caret);
        page.Run("el('i').setSelectionRange(1, 4);");
        Assert.Equal(highlighted, Fills(page.Document)); // the same selection draws the same picture
        page.Run("el('i').select();");
        Assert.NotEqual(highlighted, Fills(page.Document));
    }

    [Fact]
    public void ControlsThatHaveNoSelectionSayNullOrRefuse()
    {
        using var page = new Page("<input id=n type=number value=12><input id=c type=checkbox><input id=e type=email value=a@b.c><textarea id=t>text</textarea>");
        Assert.Equal("null,null,null", page.Run("[el('n').selectionStart, el('c').selectionStart, el('e').selectionStart].map(String).join()"));
        Assert.Equal("InvalidStateError", page.Run("try { el('n').setSelectionRange(0, 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("InvalidStateError", page.Run("try { el('n').selectionStart = 1; 'no' } catch (e) { e.name }"));
        Assert.Equal("undefined", page.Run("String(el('c').select())")); // select() does nothing
        Assert.Equal("0,4", page.Run("var t = el('t'); t.select(); t.selectionStart + ',' + t.selectionEnd"));
    }

    [Fact]
    public void SelectFiresAnEvent()
    {
        using var page = new Page("<input id=i value=abc>");
        page.Run("on('i', 'select'); el('i').select();");
        Assert.Equal("select:i", page.Log());
    }

    [Fact]
    public void SetRangeTextReplacesTextAndPlacesTheSelection()
    {
        using var page = new Page("<input id=i value=0123456789>");
        Assert.Equal("01XY456789", page.Run("var i = el('i'); i.setRangeText('XY', 2, 4); i.value"));
        Assert.Equal("01XY456789|10|10", page.Run("i.value + '|' + i.selectionStart + '|' + i.selectionEnd")); // the caret was after the range: it stays at the end
        Assert.Equal("aZ|1", page.Run("i.value = 'abc'; i.setRangeText('Z', 1, 3, 'start'); i.value + '|' + i.selectionStart"));
        Assert.Equal("aZ|2", page.Run("i.value = 'abc'; i.setRangeText('Z', 1, 3, 'end'); i.value + '|' + i.selectionEnd"));
        Assert.Equal("aZ|1,2", page.Run("i.value = 'abc'; i.setRangeText('Z', 1, 3, 'select'); i.value + '|' + i.selectionStart + ',' + i.selectionEnd"));
        // With no range, the selection is replaced.
        Assert.Equal("aXc", page.Run("i.value = 'abc'; i.setSelectionRange(1, 2); i.setRangeText('X'); i.value"));
        Assert.Equal("IndexSizeError", page.Run("try { i.setRangeText('x', 3, 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { i.setRangeText('x', 0, 1, 'sideways'); 'no' } catch (e) { e.name }"));
        Assert.Equal("Jello", page.Run("var t = document.createElement('textarea'); t.value = 'hello'; t.setRangeText('J', 0, 1); t.value"));
    }

    // ---- click and activation ----

    [Fact]
    public void ClickOnACheckboxTogglesItAndFiresClickInputAndChangeInThatOrder()
    {
        using var page = new Page("<input id=c type=checkbox>");
        page.Run("on('c', 'click'); on('c', 'input'); on('c', 'change'); el('c').click();");
        Assert.Equal("click:c input:c change:c", page.Log());
        Assert.Equal("true", page.Run("String(el('c').checked)"));
        page.Run("log.length = 0; el('c').click();");
        Assert.Equal("click:c input:c change:c", page.Log());
        Assert.Equal("false", page.Run("String(el('c').checked)"));
    }

    [Fact]
    public void ACancelledClickLeavesACheckboxAsItWas()
    {
        using var page = new Page("<input id=c type=checkbox checked>");
        page.Run("on('c', 'input'); on('c', 'change'); el('c').addEventListener('click', function (e) { e.preventDefault(); }); el('c').click();");
        Assert.Equal("true", page.Run("String(el('c').checked)"));
        Assert.Equal("", page.Log());
    }

    [Fact]
    public void ClickOnARadioChecksItAndUnchecksItsGroup()
    {
        using var page = new Page("<form id=f><input id=a type=radio name=g checked><input id=b type=radio name=g><input id=c type=radio name=g></form><input id=other type=radio name=g>");
        page.Run("on('a', 'change'); on('b', 'change'); on('b', 'input'); el('b').click();");
        Assert.Equal("false,true,false,false", page.Run("['a', 'b', 'c', 'other'].map(function (id) { return el(id).checked; }).join()"));
        Assert.Equal("input:b change:b", page.Log()); // a changed too, but a radio fires only for the one the user chose
        page.Run("log.length = 0; el('b').click();");
        Assert.Equal("", page.Log()); // already checked: nothing changes
        page.Run("el('other').click();");
        Assert.Equal("false,true,false,true", page.Run("['a', 'b', 'c', 'other'].map(function (id) { return el(id).checked; }).join()")); // another form's radio is a group of its own
    }

    [Fact]
    public void ClickDoesNothingToADisabledControlAndOnlyFiresClickOnAPlainButton()
    {
        using var page = new Page("<input id=c type=checkbox disabled><button id=b type=button>b</button><div id=d>d</div>");
        page.Run("on('c', 'click'); on('b', 'click'); on('d', 'click'); el('c').click(); el('b').click(); el('d').click();");
        Assert.Equal("click:b click:d", page.Log());
        Assert.Equal("false", page.Run("String(el('c').checked)"));
    }

    [Fact]
    public void ClickOnASubmitButtonSubmitsTheFormWithTheButtonAsSubmitter()
    {
        using var page = new Page("<form id=f action=/go method=post><input name=a value=1><button id=go name=how value=fast>Go</button></form>");
        page.Run("on('f', 'submit', function (e) { log.push('submit:' + e.submitter.id + ':' + (e instanceof SubmitEvent)); }); el('go').click();");
        Assert.Equal("submit:go:true", page.Log());
        var submitted = Assert.Single(page.Submitted);
        Assert.Equal("/go", submitted.Action);
        Assert.Equal("POST", submitted.Method);
        Assert.Equal(["a=1", "how=fast"], submitted.Data.Select(p => p.Key + "=" + p.Value));
        Assert.Equal("go", submitted.Submitter?.Id);
    }

    [Fact]
    public void ClickOnAResetButtonRestoresTheDefaultsAfterAnEventThatCanBeCancelled()
    {
        using var page = new Page("<form id=f><input id=i value=start><input id=c type=checkbox><button id=r type=reset>r</button></form>");
        page.Run("el('i').value = 'edited'; el('c').checked = true; on('f', 'reset'); el('r').click();");
        Assert.Equal("reset:f", page.Log());
        Assert.Equal("start,false", page.Run("el('i').value + ',' + el('c').checked"));
        page.Run("el('i').value = 'edited again'; el('f').addEventListener('reset', function (e) { e.preventDefault(); }); el('r').click();");
        Assert.Equal("edited again", page.Run("el('i').value"));
    }

    [Fact]
    public void ClickOnALabelClicksItsControl()
    {
        using var page = new Page("<label id=l for=c>box</label><input id=c type=checkbox><label id=w><input id=inner type=checkbox>wrapped</label>");
        page.Run("on('c', 'change'); on('inner', 'change'); el('l').click(); el('w').click();");
        Assert.Equal("change:c change:inner", page.Log());
    }

    [Fact]
    public void FocusMovesFocusAndFiresFocusEvents()
    {
        using var page = new Page("<input id=a><input id=b>");
        page.Run("on('a', 'focus'); on('a', 'blur'); on('b', 'focus'); el('a').focus();");
        Assert.Equal("true", page.Run("String(document.activeElement === el('a'))"));
        page.Run("el('b').focus();");
        Assert.Equal("focus:a blur:a focus:b", page.Log());
        Assert.Equal("true", page.Run("String(document.activeElement === el('b'))"));
        page.Run("el('b').blur();");
        Assert.Equal("true", page.Run("String(document.activeElement === document.body)"));
    }

    // ---- validation ----

    [Fact]
    public void ValidityReportsEachConstraint()
    {
        using var page = new Page(@"<input id=req required><input id=mail type=email value='no at'><input id=url type=url value='x'>
            <input id=pat pattern='[a-z]{3}' value='ab1'><input id=num type=number min=2 max=4 step=1 value=7><input id=step type=number step=0.5 min=0 value=1.25>
            <input id=ok type=text value=fine><input id=box type=checkbox required><select id=sel required><option value=''>pick</option><option value=1>one</option></select>");
        Assert.Equal("valueMissing", Flags(page, "req"));
        Assert.Equal("typeMismatch", Flags(page, "mail"));
        Assert.Equal("typeMismatch", Flags(page, "url"));
        Assert.Equal("patternMismatch", Flags(page, "pat"));
        Assert.Equal("rangeOverflow", Flags(page, "num"));
        Assert.Equal("stepMismatch", Flags(page, "step"));
        Assert.Equal("", Flags(page, "ok"));
        Assert.Equal("valueMissing", Flags(page, "box"));
        Assert.Equal("valueMissing", Flags(page, "sel"));
        page.Run("el('num').value = 1; el('mail').value = 'me@example.com'; el('req').value = 'x'; el('box').checked = true; el('sel').value = '1';");
        Assert.Equal("rangeUnderflow", Flags(page, "num"));
        Assert.Equal("", Flags(page, "mail"));
        Assert.Equal("", Flags(page, "req"));
        Assert.Equal("", Flags(page, "box"));
        Assert.Equal("", Flags(page, "sel"));
        Assert.Equal("true,false,false", page.Run("[el('ok').validity.valid, el('req').validity.valueMissing, el('num').validity.valid].join()"));
        Assert.Equal("true", page.Run("String(el('ok').validity === el('ok').validity)"));
    }

    private static string Flags(Page page, string id) => page.Run(
        $"['valueMissing', 'typeMismatch', 'patternMismatch', 'tooLong', 'tooShort', 'rangeUnderflow', 'rangeOverflow', 'stepMismatch', 'badInput', 'customError'].filter(function (f) {{ return el('{id}').validity[f]; }}).join()");

    [Fact]
    public void ARequiredRadioIsSatisfiedByAnyRadioOfItsGroup()
    {
        using var page = new Page("<form><input id=a type=radio name=g required><input id=b type=radio name=g></form>");
        Assert.Equal("valueMissing", Flags(page, "a"));
        page.Run("el('b').checked = true;");
        Assert.Equal("", Flags(page, "a"));
    }

    [Fact]
    public void ValidationMessagesAndWillValidate()
    {
        using var page = new Page("<input id=req required><input id=off required disabled><input id=ro required readonly><input id=hid type=hidden required><input id=btn type=button><input id=mail type=email value=bad>");
        Assert.Equal("Please fill out this field.", page.Run("el('req').validationMessage"));
        Assert.Equal("true,false,false,false,false,true", page.Run("['req', 'off', 'ro', 'hid', 'btn', 'mail'].map(function (id) { return el(id).willValidate; }).join()"));
        Assert.Equal("", page.Run("el('off').validationMessage"));
        Assert.Contains("@", page.Run("el('mail').validationMessage"));
        Assert.Equal("true", page.Run("String(el('off').checkValidity())"));
    }

    [Fact]
    public void ACustomValidityMessageBlocksTheControlUntilItIsCleared()
    {
        using var page = new Page("<form id=f><input id=i value=fine></form>");
        Assert.Equal("true,true", page.Run("var i = el('i'); i.checkValidity() + ',' + el('f').checkValidity()"));
        page.Run("i.setCustomValidity('nope');");
        Assert.Equal("false,false,true,nope", page.Run("i.checkValidity() + ',' + el('f').checkValidity() + ',' + i.validity.customError + ',' + i.validationMessage"));
        page.Run("i.setCustomValidity('');");
        Assert.Equal("true,", page.Run("i.checkValidity() + ',' + i.validationMessage"));
        Assert.Equal("TypeError", page.Run("try { i.setCustomValidity(); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void CheckValidityFiresACancelableInvalidEventThatDoesNotBubbleAndReportValidityFocuses()
    {
        using var page = new Page("<form id=f><input id=a required><input id=b required><input id=c></form>");
        page.Run("on('a', 'invalid', function (e) { log.push('invalid:a:' + e.cancelable + ':' + e.bubbles); }); on('b', 'invalid'); el('f').addEventListener('invalid', function () { log.push('bubbled'); });");
        Assert.Equal("false", page.Run("String(el('a').checkValidity())"));
        Assert.Equal("invalid:a:true:false", page.Log());
        page.Run("log.length = 0;");
        Assert.Equal("false", page.Run("String(el('f').checkValidity())"));
        Assert.Equal("invalid:a:true:false invalid:b", page.Log());
        page.Run("log.length = 0; el('f').reportValidity();");
        Assert.Equal("true", page.Run("String(document.activeElement === el('a'))"));
        Assert.Equal("true", page.Run("String(el('c').checkValidity())"));
    }

    // ---- submission ----

    [Fact]
    public void RequestSubmitValidatesThenFiresSubmitAndSubmitDoesNeither()
    {
        using var page = new Page("<form id=f action=/a><input id=i name=n required></form>");
        page.Run("on('f', 'submit'); on('i', 'invalid');");
        page.Run("el('f').requestSubmit();");
        Assert.Equal("invalid:i", page.Log());
        Assert.Empty(page.Submitted); // invalid: no submit event, no submission
        page.Run("log.length = 0; el('f').submit();");
        Assert.Equal("", page.Log()); // submit() fires no event and validates nothing...
        Assert.Single(page.Submitted); // ...and goes
        page.Run("el('i').value = 'x'; el('f').requestSubmit();");
        Assert.Equal("submit:f", page.Log());
        Assert.Equal(2, page.Submitted.Count);
    }

    [Fact]
    public void ACancelledSubmitEventStopsTheSubmission()
    {
        using var page = new Page("<form id=f><input name=n value=v></form>");
        page.Run("el('f').addEventListener('submit', function (e) { e.preventDefault(); log.push('stopped'); }); el('f').requestSubmit();");
        Assert.Equal("stopped", page.Log());
        Assert.Empty(page.Submitted);
    }

    [Fact]
    public void NoValidateAndFormNoValidateSkipValidation()
    {
        using var page = new Page("<form id=a novalidate><input required name=x><button id=ba>go</button></form><form id=b><input required name=y><button id=bb formnovalidate name=how value=skip>go</button><button id=bc>go</button></form>");
        page.Run("el('a').requestSubmit(); el('bb').click();");
        Assert.Equal(2, page.Submitted.Count);
        Assert.Equal("how=skip", string.Join(",", page.Submitted[1].Data.Select(p => p.Key + "=" + p.Value).Where(p => p.StartsWith("how"))));
        page.Run("el('bc').click();");
        Assert.Equal(2, page.Submitted.Count); // the required field stops a plain submit button
        Assert.Equal("true", page.Run("String(el('a').noValidate && !el('b').noValidate)"));
    }

    [Fact]
    public void RequestSubmitChecksItsSubmitterArgument()
    {
        using var page = new Page("<form id=f><button id=go>go</button><button id=plain type=button>x</button></form><form id=g><button id=foreign>f</button></form>");
        Assert.Equal("TypeError", page.Run("try { el('f').requestSubmit(el('plain')); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { el('f').requestSubmit({}); 'no' } catch (e) { e.name }"));
        Assert.Equal("NotFoundError", page.Run("try { el('f').requestSubmit(el('foreign')); 'no' } catch (e) { e.name }"));
        page.Run("el('f').requestSubmit(el('go'));");
        Assert.Equal("go", page.Submitted.Single().Submitter?.Id);
    }

    [Fact]
    public void FormResetRestoresDefaultsAfterACancelableEvent()
    {
        using var page = new Page("<form id=f><input id=i value=a><textarea id=t>start</textarea><input id=c type=checkbox checked><select id=s><option value=x>x</option><option value=y selected>y</option></select></form>");
        page.Run("el('i').value = 'b'; el('t').value = 'changed'; el('c').checked = false; el('s').value = 'x'; on('f', 'reset'); el('f').reset();");
        Assert.Equal("reset:f", page.Log());
        Assert.Equal("a,start,true,y", page.Run("[el('i').value, el('t').value, el('c').checked, el('s').value].join()"));
    }

    // ---- the entry list and FormData ----

    [Fact]
    public void FormDataHoldsTheEntryListOfAMixedForm()
    {
        using var page = new Page(@"<form id=f>
            <input name=text value=hello><input name=empty value=''><input value=unnamed><input name=off value=x disabled>
            <textarea name=area>lines</textarea><input type=hidden name=h value=secret>
            <input type=checkbox name=on value=yes checked><input type=checkbox name=unchecked value=no><input type=checkbox name=defaulted checked>
            <input type=radio name=pick value=a><input type=radio name=pick value=b checked>
            <select name=one><option value=1>1</option><option value=2 selected>2</option></select>
            <select name=first><option value=p>p</option><option value=q>q</option></select>
            <select name=many multiple><option value=m1 selected>m1</option><option value=m2>m2</option><option value=m3 selected>m3</option></select>
            <input type=submit name=send value=Send><button name=b value=btn>B</button><input type=reset name=r><input type=button name=pb value=x>
            </form>");
        Assert.Equal("text=hello,empty=,area=lines,h=secret,on=yes,defaulted=on,pick=b,one=2,first=p,many=m1,many=m3",
            page.Run("Array.from(new FormData(el('f')).entries()).map(function (e) { return e.join('='); }).join()"));
        // With a submitter, its own name and value come along.
        Assert.Equal("send=Send", page.Run("Array.from(new FormData(el('f'), document.querySelector('[type=submit]'))).map(function (e) { return e.join('='); }).filter(function (e) { return e.indexOf('send') === 0; }).join()"));
        Assert.Equal("b=btn", page.Run("Array.from(new FormData(el('f'), document.querySelector('button'))).map(function (e) { return e.join('='); }).filter(function (e) { return e.indexOf('b=') === 0; }).join()"));
    }

    [Fact]
    public void FormDataCurrentValuesNotTheDefaultsAndTheFormAttributeAssociates()
    {
        using var page = new Page("<form id=f><input id=i name=a value=default></form><input name=outside form=f value=joined><form id=other><input name=elsewhere value=no></form><input name=nothing value=no>");
        page.Run("el('i').value = 'typed';");
        Assert.Equal("a=typed,outside=joined", page.Run("Array.from(new FormData(el('f'))).map(function (e) { return e.join('='); }).join()"));
    }

    [Fact]
    public void ConstructingFormDataFiresAFormdataEventWhichMayChangeIt()
    {
        using var page = new Page("<form id=f><input name=a value=1></form>");
        page.Run("el('f').addEventListener('formdata', function (e) { log.push('formdata:' + (e instanceof FormDataEvent) + ':' + e.bubbles); e.formData.append('added', 'yes'); }); var fd = new FormData(el('f'));");
        Assert.Equal("formdata:true:true", page.Log());
        Assert.Equal("a=1,added=yes", page.Run("Array.from(fd).map(function (e) { return e.join('='); }).join()"));
    }

    [Fact]
    public void FormDataRefusesWhatIsNotAForm()
    {
        using var page = new Page("<div id=d></div><form id=f><button id=b>b</button></form><form id=g><button id=gb>b</button></form>");
        Assert.Equal("TypeError", page.Run("try { new FormData(el('d')); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { new FormData({}); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { new FormData(el('f'), el('d')); 'no' } catch (e) { e.name }"));
        Assert.Equal("NotFoundError", page.Run("try { new FormData(el('f'), el('gb')); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { FormData(); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void FormDataBuiltByScriptFollowsTheInterface()
    {
        using var page = new Page("<p></p>");
        Assert.Equal("a=1,b=2,a=3", page.Run("var fd = new FormData(); fd.append('a', 1); fd.append('b', 2); fd.append('a', 3); Array.from(fd).map(function (e) { return e.join('='); }).join()"));
        Assert.Equal("1,1|true,false", page.Run("fd.get('a') + ',' + fd.getAll('a')[0] + '|' + fd.has('b') + ',' + fd.has('z')"));
        Assert.Equal("a=9,b=2", page.Run("fd.set('a', 9); Array.from(fd).map(function (e) { return e.join('='); }).join()"));
        Assert.Equal("b", page.Run("fd.delete('a'); Array.from(fd.keys()).join()"));
        Assert.Equal("b=2", page.Run("var out = []; fd.forEach(function (v, k) { out.push(k + '=' + v); }); out.join()"));
        Assert.Equal("null", page.Run("String(fd.get('missing'))"));
        Assert.Equal("[object FormData]", page.Run("Object.prototype.toString.call(fd)"));
        Assert.Equal("TypeError", page.Run("try { fd.append('only'); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void FormElementsListsTheControlsInOrder()
    {
        using var page = new Page("<form id=f><input id=a name=first><fieldset><select id=b name=second></select></fieldset><button id=c>c</button><img src=x><input id=d type=image></form><input id=e form=f name=third><input id=out name=out>");
        Assert.Equal("5|a,,b,c,e", page.Run("var f = el('f'); f.elements.length + '|' + Array.from(f.elements).map(function (e) { return e.id; }).join()")); // the fieldset is listed; the image input is not
        Assert.Equal("a,second", page.Run("f.elements.namedItem('first').id + ',' + f.elements.second.name"));
        Assert.Equal("c", page.Run("f.elements.item(3).id"));
        Assert.Equal("f", page.Run("el('a').form.id"));
        Assert.Equal("f", page.Run("el('e').form.id")); // by its form attribute
        Assert.Equal("null", page.Run("String(el('out').form)"));
    }

    [Fact]
    public void ABareDocumentTreeGetsTheSameFormBehaviourWithoutADocument()
    {
        var node = TreeBuilder.Parse("<form id=f><input id=i name=n value=vvvv required><input id=c type=checkbox></form>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, node);
        Assert.Equal("true", engine.Evaluate("var i = document.getElementById('i'); String(i.checkValidity())").ToString());
        Assert.Equal("n=vvvv", engine.Evaluate("Array.from(new FormData(document.getElementById('f'))).map(function (e) { return e.join('='); }).join()").ToString());
        Assert.Equal("1,3", engine.Evaluate("i.setSelectionRange(1, 3); i.selectionStart + ',' + i.selectionEnd").ToString());
        Assert.Equal("click,input,change", engine.Evaluate("var seen = []; var c = document.getElementById('c'); ['click', 'input', 'change'].forEach(function (t) { c.addEventListener(t, function () { seen.push(t); }); }); c.click(); seen.join()").ToString());
    }
}
