namespace Folio.Dom;

internal readonly record struct QualifiedName(Atom Namespace, Atom LocalName);

/// <param name="Name">The qualified name (<c>xlink:href</c> for a prefixed attribute).</param>
/// <param name="Namespace">None for ordinary attributes; set for the parser's adjusted foreign attributes.</param>
internal readonly record struct Attribute(Atom Name, string Value, Atom Namespace = default);

/// <summary>https://dom.spec.whatwg.org/#interface-element</summary>
internal class ElementNode(DocumentNode ownerDocument, QualifiedName name) : ContainerNode(ownerDocument)
{
    private Attribute[] _attributes = [];

    public QualifiedName Name { get; } = name;
    public string LocalName => OwnerDocument.TextOf(Name.LocalName);
    public string NamespaceUri => OwnerDocument.TextOf(Name.Namespace);

    /// <summary>Exact-size, in source order; elements rarely have more than a few.</summary>
    public ReadOnlySpan<Attribute> Attributes => _attributes;

    /// <summary>The <c>id</c> attribute, parsed when set.</summary>
    public Atom Id { get; private set; }

    /// <summary>The computed style, owned by the style module (typed there, so the DOM layer does not reference it).</summary>
    internal object? StyleData { get; set; }

    /// <summary>The bitmap of a canvas element, an <see cref="Imaging.ICanvasSurface"/> installed by the scripting layer (typed loosely: Dom ranks below Imaging).</summary>
    internal object? CanvasSurface { get; set; }

    /// <summary>Media state installed by the scripting layer for native controls and cue painting.</summary>
    public IMediaPresentation? MediaPresentation { get; internal set; }

    /// <summary>The current form control value (separate from the 'value' content attribute).</summary>
    public string? Value { get; set; }

    /// <summary>The display text of a text control as presented on screen (masked for password, etc.).</summary>
    internal string? DisplayValue { get; set; }

    /// <summary>The current checked state of a checkbox or radio button (separate from the 'checked' content attribute).</summary>
    public bool? Checked { get; set; }

    /// <summary>The indeterminate state of a checkbox.</summary>
    public bool Indeterminate { get; set; }

    /// <summary>The current selected state of an option element (separate from the 'selected' content attribute).</summary>
    public bool? Selected { get; set; }

    /// <summary>Whether this checkbox, radio, or option is currently checked/selected.</summary>
    public bool IsChecked => Checked ?? (GetAttribute("checked") is not null);

    /// <summary>Whether this option is currently selected.</summary>
    public bool IsSelected => Selected ?? (GetAttribute("selected") is not null);

    /// <summary>Whether this form control is disabled (directly or by an ancestor fieldset).</summary>
    internal bool IsDisabled => IsDisabledControl(this);

    private string? _sheetText;

    /// <summary>
    /// The rules of a <c>style</c> or <c>link</c> element's sheet as the CSSOM left them (insertRule, deleteRule, a rule's
    /// style): what the cascade reads instead of the element's text or the loaded file, until the page changes the
    /// element's text, which clears it. The DOM text is never rewritten by the CSSOM, as in a browser.
    /// </summary>
    internal string? SheetText
    {
        get => _sheetText;
        set
        {
            _sheetText = value;
            OnMutated(MutationKind.Attributes);
        }
    }

    /// <summary>Drops the CSSOM's override without queueing a restyle of its own (the DOM change that clears it does).</summary>
    internal void ClearSheetText() => _sheetText = null;

    /// <summary>Tracks whether a script element has already executed or started execution (HTML §4.12.1.1 "already started").</summary>
    internal bool ScriptAlreadyStarted { get; set; }

    internal static bool IsDisabledControl(ElementNode element)
    {
        if (element.Name.Namespace != Namespaces.Html)
            return false;

        var isControl = element.LocalName is "button" or "input" or "select" or "textarea" or "optgroup" or "option" or "fieldset";
        if (!isControl)
            return false;

        if (element.GetAttribute("disabled") is not null)
            return true;

        if (element.LocalName == "option" && element.Parent is ElementNode { LocalName: "optgroup" } group && group.GetAttribute("disabled") is not null)
            return true;

        if (element.LocalName is "optgroup" or "option")
            return false;

        Node child = element;
        for (var ancestor = element.Parent as ElementNode; ancestor is not null; child = ancestor, ancestor = ancestor.Parent as ElementNode)
        {
            if (ancestor.Name.Namespace == Namespaces.Html && ancestor.LocalName == "fieldset" && ancestor.GetAttribute("disabled") is not null)
            {
                var firstLegend = ancestor.Children.OfType<ElementNode>().FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "legend");
                if (child != firstLegend)
                    return true;
            }
        }

        return false;
    }

    /// <summary>The <c>class</c> attribute split on ASCII whitespace, parsed when set.</summary>
    public Atom[] Classes { get; private set; } = [];

    public string? GetAttribute(string name)
    {
        var atom = OwnerDocument.Find(name);
        if (atom.IsNone)
            return null;
        foreach (var attribute in _attributes)
        {
            if (attribute.Name == atom)
                return attribute.Value;
        }
        return null;
    }

    /// <summary>https://dom.spec.whatwg.org/#dom-element-setattribute (name used as given; the parser lowercases).</summary>
    public void SetAttribute(string name, string value)
    {
        var atom = OwnerDocument.Intern(name);
        var index = Array.FindIndex(_attributes, a => a.Name == atom);
        var oldValue = index >= 0 ? _attributes[index].Value : null;
        if (index >= 0)
        {
            _attributes[index] = new Attribute(atom, value);
        }
        else
        {
            Array.Resize(ref _attributes, _attributes.Length + 1);
            _attributes[^1] = new Attribute(atom, value);
        }

        Reflect(name, value);
        OnMutated(MutationKind.Attributes);
        OwnerDocument.MutationRecorder?.Attribute(this, name, oldValue);
        if (IsHandlerAttributeName(name))
            OwnerDocument.NotifyHandlerAttributeChanged(this, name, value);
    }

    private static bool IsHandlerAttributeName(string name) =>
        name.Length > 2 && name[0] is 'o' or 'O' && name[1] is 'n' or 'N';

    /// <summary>Removes an attribute from the element.</summary>
    public bool RemoveAttribute(string name)
    {
        var atom = OwnerDocument.Find(name);
        if (atom.IsNone)
            return false;
        var index = Array.FindIndex(_attributes, a => a.Name == atom);
        if (index < 0)
            return false;
        var oldValue = _attributes[index].Value;
        var newAttrs = new Attribute[_attributes.Length - 1];
        Array.Copy(_attributes, 0, newAttrs, 0, index);
        Array.Copy(_attributes, index + 1, newAttrs, index, _attributes.Length - index - 1);
        _attributes = newAttrs;
        Reflect(name, "");
        OnMutated(MutationKind.Attributes);
        OwnerDocument.MutationRecorder?.Attribute(this, name, oldValue);
        if (IsHandlerAttributeName(name))
            OwnerDocument.NotifyHandlerAttributeChanged(this, name, null);
        return true;
    }

    /// <summary>Sets all attributes of a new element at once (the parser; names are already unique).</summary>
    internal void SetParsedAttributes(Attribute[] attributes)
    {
        _attributes = attributes;
        foreach (var attribute in attributes)
        {
            if (attribute.Namespace.IsNone)
                Reflect(OwnerDocument.TextOf(attribute.Name), attribute.Value);
        }
    }

    private void Reflect(string name, string value)
    {
        switch (name)
        {
            case "id":
                Id = value.Length == 0 ? Atom.None : OwnerDocument.Intern(value);
                break;
            case "class":
                Classes = value.Length == 0 ? [] : value.Split(AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .Select(OwnerDocument.Intern)
                    .ToArray();
                break;
        }
    }

    public override Node Clone(bool deep = false)
    {
        var copy = OwnerDocument.CreateElement(Name.Namespace, LocalName);
        if (_attributes.Length > 0)
        {
            var attrs = new Attribute[_attributes.Length];
            Array.Copy(_attributes, attrs, _attributes.Length);
            copy.SetParsedAttributes(attrs);
        }
        if (deep)
        {
            for (var child = FirstChild; child is not null; child = child.NextSibling)
                copy.AppendChild(child.Clone(deep: true));
            if (this is TemplateElement template && copy is TemplateElement copyTemplate)
            {
                for (var child = template.Content.FirstChild; child is not null; child = child.NextSibling)
                    copyTemplate.Content.AppendChild(child.Clone(deep: true));
            }
        }
        return copy;
    }

    // https://infra.spec.whatwg.org/#ascii-whitespace
    internal static readonly char[] AsciiWhitespace = ['\t', '\n', '\f', '\r', ' '];
}

/// <summary>
/// https://html.spec.whatwg.org/multipage/scripting.html#the-template-element: its parsed contents are kept
/// in an inert fragment, outside the tree.
/// </summary>
internal sealed class TemplateElement(DocumentNode ownerDocument, QualifiedName name) : ElementNode(ownerDocument, name)
{
    public DocumentFragment Content { get; } = new(ownerDocument);
}
