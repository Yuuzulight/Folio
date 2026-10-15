using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// DOM §4.3 mutation observers (issue #495): records are queued by the tree, attribute and character data algorithms and
/// delivered at the microtask checkpoint, per observer in creation order, with transient registrations for removed subtrees.
/// </summary>
public class MutationObserverTests
{
    private const string Page = "<html><body><div id='box'><p id='a'>one</p><p id='b'>two</p></div></body></html>";

    // Runs the script (which fills `log`), lets the microtasks run, and returns what the log holds.
    private static string Run(string script, string html = Page)
    {
        var document = TreeBuilder.Parse(html);
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("var log = [];" + script);
        engine.PerformMicrotaskCheckpoint();
        return engine.Evaluate("log.join('|')").AsString()!;
    }

    private const string Describe = "function d(r) { return r.type + ':' + r.target.id + ':+' + r.addedNodes.length + '-' + r.removedNodes.length"
        + " + ':' + (r.attributeName ?? '') + ':' + (r.oldValue ?? '') + ':' + (r.previousSibling && r.previousSibling.id) + ':' + (r.nextSibling && r.nextSibling.id); }";

    [Fact]
    public void ChildListAppendRecordsTheAddedNodeAndItsSiblings()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            new MutationObserver(rs => rs.forEach(r => log.push(d(r)))).observe(box, { childList: true });
            const c = document.createElement('i'); c.id = 'c';
            box.appendChild(c);");
        Assert.Equal("childList:box:+1-0:::b:null", log);
    }

    [Fact]
    public void ChildListInsertBeforeRecordsItsNeighbours()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            new MutationObserver(rs => rs.forEach(r => log.push(d(r)))).observe(box, { childList: true });
            const c = document.createElement('i'); c.id = 'c';
            box.insertBefore(c, document.getElementById('b'));");
        Assert.Equal("childList:box:+1-0:::a:b", log);
    }

    [Fact]
    public void ChildListRemoveRecordsTheRemovedNodeAndTheSiblingsItLeft()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            new MutationObserver(rs => rs.forEach(r => log.push(d(r)))).observe(box, { childList: true });
            box.removeChild(document.getElementById('a'));");
        Assert.Equal("childList:box:+0-1:::null:b", log);
    }

    [Fact]
    public void ChildListReplaceChildIsOneRecordWithBothSides()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            new MutationObserver(rs => rs.forEach(r => log.push(d(r)))).observe(box, { childList: true });
            const c = document.createElement('i'); c.id = 'c';
            box.replaceChild(c, document.getElementById('a'));");
        Assert.Equal("childList:box:+1-1:::null:b", log);
    }

    [Fact]
    public void TextContentAndInnerHtmlReplacingChildrenAreOneRecord()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            new MutationObserver(rs => rs.forEach(r => log.push(d(r)))).observe(box, { childList: true });
            box.textContent = 'hello';
            box.innerHTML = '<b>x</b><i>y</i>';");
        Assert.Equal("childList:box:+1-2:::null:null|childList:box:+2-1:::null:null", log);
    }

    [Fact]
    public void AttributeRecordsCarryTheOldValueOnlyWhenAsked()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            box.setAttribute('title', 'first');
            new MutationObserver(rs => rs.forEach(r => log.push('plain ' + d(r)))).observe(box, { attributes: true });
            new MutationObserver(rs => rs.forEach(r => log.push('old ' + d(r)))).observe(box, { attributeOldValue: true });
            box.setAttribute('title', 'second');
            box.removeAttribute('title');");
        Assert.Equal(
            "plain attributes:box:+0-0:title::null:null|plain attributes:box:+0-0:title::null:null|"
            + "old attributes:box:+0-0:title:first:null:null|old attributes:box:+0-0:title:second:null:null",
            log);
    }

    [Fact]
    public void AttributeFilterSelectsNames()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            new MutationObserver(rs => rs.forEach(r => log.push(r.attributeName))).observe(box, { attributeFilter: ['title', 'lang'] });
            box.setAttribute('class', 'x');
            box.setAttribute('title', 'y');
            box.setAttribute('lang', 'en');
            box.setAttribute('dir', 'ltr');");
        Assert.Equal("title|lang", log);
    }

    [Fact]
    public void CharacterDataRecordsTheOldValueWhenAsked()
    {
        var log = Run(Describe + @"
            const text = document.getElementById('a').firstChild;
            new MutationObserver(rs => rs.forEach(r => log.push(r.type + ':' + r.oldValue)))
                .observe(text, { characterDataOldValue: true });
            text.data = 'changed';
            text.data = 'again';");
        Assert.Equal("characterData:one|characterData:changed", log);
    }

    [Fact]
    public void SubtreeDecidesWhetherDescendantsAreObserved()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            new MutationObserver(rs => rs.forEach(r => log.push('flat ' + r.target.id))).observe(box, { attributes: true });
            new MutationObserver(rs => rs.forEach(r => log.push('deep ' + r.target.id))).observe(box, { attributes: true, subtree: true });
            document.getElementById('a').setAttribute('x', '1');
            box.setAttribute('x', '1');");
        // Each observer gets its own records, in the order observers were created.
        Assert.Equal("flat box|deep a|deep box", log);
    }

    [Fact]
    public void ARemovedSubtreeIsObservedThroughTransientRegistrationsUntilDelivery()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            const a = document.getElementById('a');
            const observer = new MutationObserver(rs => rs.forEach(r => log.push(r.type + ':' + r.target.id + ':' + (r.attributeName ?? ''))));
            observer.observe(box, { attributes: true, childList: true, subtree: true });
            box.removeChild(a);
            a.setAttribute('x', '1');   // still observed: the removed node carries a transient registration
            Promise.resolve().then(() => { a.setAttribute('y', '1'); });  // after delivery: the transient registration is gone
            ");
        Assert.Equal("childList:box:|attributes:a:x", log);
    }

    [Fact]
    public void DeliveryHappensAtTheMicrotaskCheckpointInOrderWithPromiseCallbacks()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            new MutationObserver(() => log.push('mutation')).observe(box, { attributes: true });
            Promise.resolve().then(() => log.push('before'));
            box.setAttribute('x', '1');
            Promise.resolve().then(() => log.push('after'));
            log.push('sync');");
        Assert.Equal("sync|before|mutation|after", log);
    }

    [Fact]
    public void TakeRecordsEmptiesTheQueueSoNothingIsDelivered()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            const observer = new MutationObserver(() => log.push('delivered'));
            observer.observe(box, { attributes: true });
            box.setAttribute('x', '1');
            box.setAttribute('y', '1');
            const taken = observer.takeRecords();
            log.push('taken ' + taken.length + ' ' + taken.map(r => r.attributeName).join(','));
            log.push('again ' + observer.takeRecords().length);");
        Assert.Equal("taken 2 x,y|again 0", log);
    }

    [Fact]
    public void ObservingATargetAgainReplacesItsOptions()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            const observer = new MutationObserver(rs => rs.forEach(r => log.push(r.type)));
            observer.observe(box, { attributes: true });
            observer.observe(box, { childList: true });
            box.setAttribute('x', '1');
            box.appendChild(document.createElement('i'));");
        Assert.Equal("childList", log);
    }

    [Fact]
    public void DisconnectStopsObservingAndDropsPendingRecords()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            const observer = new MutationObserver(() => log.push('delivered'));
            observer.observe(box, { attributes: true });
            box.setAttribute('x', '1');
            observer.disconnect();
            box.setAttribute('y', '1');
            log.push('end');");
        Assert.Equal("end", log);
    }

    [Fact]
    public void ObserversAreNotifiedInCreationOrderAndGetTheirObserverAsTheSecondArgument()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            const first = new MutationObserver((rs, o) => log.push('first ' + (o === first)));
            const second = new MutationObserver((rs, o) => log.push('second ' + (o === second)));
            second.observe(box, { attributes: true });
            first.observe(box, { attributes: true });
            box.setAttribute('x', '1');");
        Assert.Equal("first true|second true", log);
    }

    [Fact]
    public void InvalidOptionsAndTargetsThrowTypeErrors()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            const observer = new MutationObserver(() => {});
            const attempt = (name, f) => { try { f(); log.push(name + ' ok'); } catch (e) { log.push(name + ' ' + e.name); } };
            attempt('none', () => observer.observe(box, {}));
            attempt('missing', () => observer.observe(box));
            attempt('oldNoAttributes', () => observer.observe(box, { attributes: false, attributeOldValue: true }));
            attempt('filterNoAttributes', () => observer.observe(box, { attributes: false, attributeFilter: ['x'] }));
            attempt('dataOldNoData', () => observer.observe(box, { characterData: false, characterDataOldValue: true }));
            attempt('target', () => observer.observe({}, { attributes: true }));
            attempt('impliedAttributes', () => observer.observe(box, { attributeOldValue: true }));
            attempt('callback', () => new MutationObserver(5));");
        Assert.Equal("none TypeError|missing TypeError|oldNoAttributes TypeError|filterNoAttributes TypeError|dataOldNoData TypeError|target TypeError|impliedAttributes ok|callback TypeError", log);
    }

    [Fact]
    public void MovingANodeRecordsItsRemovalFromTheOldParentAndTheInsertionInTheNew()
    {
        var log = Run(Describe + @"
            const box = document.getElementById('box');
            const other = document.createElement('section'); other.id = 'other';
            document.body.appendChild(other);
            new MutationObserver(rs => rs.forEach(r => log.push(d(r)))).observe(document.body, { childList: true, subtree: true });
            other.appendChild(document.getElementById('a'));");
        Assert.Equal("childList:box:+0-1:::null:b|childList:other:+1-0:::null:null", log);
    }
}
