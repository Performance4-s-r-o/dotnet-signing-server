using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Options;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Exceptions;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Xobject;
using iText.Signatures;

namespace DotNetSigningServer.Services;

/// <summary>
/// Flattens the filled AcroForm fields of a document into page content and removes its active
/// content, so that <see cref="PdfSafetyGuard"/> accepts it with <c>RejectActiveContent</c> on
/// (#180). The document is rewritten, not appended to: iText cannot flatten in append mode, and a
/// rewrite breaks every earlier signature — so a signed document is refused before anything changes.
///
/// Kept: empty signature fields (<c>/FT /Sig</c>, without their actions), link annotations with
/// their URI / GoTo actions, embedded files, the tag tree. Removed: the JavaScript name tree,
/// JavaScript and Launch actions wherever the guard looks for them (and in bookmarks),
/// <c>/AA</c> trigger actions, XFA of a hybrid form, Reader extensions (<c>/Perms/UR3</c>).
/// </summary>
public static class PdfFormFlattener
{
    public const string AlreadySignedCode = "PDF_ALREADY_SIGNED";
    public const string XfaDynamicCode = "PDF_XFA_DYNAMIC";

    /// <summary>What was done; never field names or values (they are the signer's data).</summary>
    public sealed record Report(int FlattenedFields, IReadOnlyList<string> Removed)
    {
        public bool Changed => FlattenedFields > 0 || Removed.Count > 0;
    }

    public sealed record Result(byte[] Pdf, Report Report);

    // Stable identifiers of what was removed, part of the API answer.
    public static class RemovedKinds
    {
        public const string DocumentJavaScript = "document-javascript";
        public const string OpenAction = "open-action";
        public const string DocumentActions = "document-actions";
        public const string PageActions = "page-actions";
        public const string AnnotationActions = "annotation-actions";
        public const string FieldActions = "field-actions";
        public const string OutlineActions = "outline-actions";
        public const string Xfa = "xfa";
        public const string ReaderExtensions = "reader-extensions";
    }

    private const int MaxDepth = 32;

    /// <summary>
    /// Flattens <paramref name="source"/>. Throws <see cref="ApiValidationException"/> with
    /// <see cref="AlreadySignedCode"/> or <see cref="XfaDynamicCode"/> before any change. When there
    /// was nothing to flatten or remove, the source bytes come back unchanged.
    /// </summary>
    public static Result Flatten(byte[] source, LimitsOptions limits)
    {
        var handler = new MemoryLimitsAwareHandler();
        handler.SetMaxSizeOfSingleDecompressedPdfStream((int)Math.Min(limits.PdfMaxStreamBytes, int.MaxValue));
        handler.SetMaxSizeOfDecompressedPdfStreamsSum(limits.PdfMaxTotalStreamBytes);
        handler.SetMaxNumberOfElementsInXrefStructure(limits.PdfMaxObjects);

        var output = new MemoryStream();
        var removed = new SortedSet<string>(StringComparer.Ordinal);
        var flattened = 0;
        PdfOpenCounter.Record();
        using (var reader = new PdfReader(new MemoryStream(source), new ReaderProperties().SetMemoryLimitsAwareHandler(handler)))
        using (var writer = new PdfWriter(output, new WriterProperties().SetFullCompressionMode(true)))
        {
            writer.SetCloseStream(false);
            var document = new PdfDocument(reader, writer);
            var completed = false;
            try
            {
                // Any signature, document timestamps and a certification (DocMDP) included: the
                // rewrite would break it.
                var catalog = document.GetCatalog().GetPdfObject();
                if (new SignatureUtil(document).GetSignatureNames().Count > 0
                    || catalog.GetAsDictionary(PdfName.Perms)?.ContainsKey(PdfName.DocMDP) == true
                    || HasSignatureValue(catalog.GetAsDictionary(PdfName.AcroForm)?.GetAsArray(PdfName.Fields), null, 0))
                {
                    throw new ApiValidationException(AlreadySignedCode, "document carries a signature");
                }

                var form = PdfAcroForm.GetAcroForm(document, false);
                if (form != null)
                {
                    flattened = FlattenForm(document, catalog, form, removed);
                }
                RemoveActiveContent(document, catalog, removed);

                var perms = catalog.GetAsDictionary(PdfName.Perms);
                if (perms != null && (perms.Remove(new PdfName("UR3")) != null | perms.Remove(new PdfName("UR")) != null))
                {
                    removed.Add(RemovedKinds.ReaderExtensions);
                    if (perms.Size() == 0) catalog.Remove(PdfName.Perms);
                }
                completed = true;
            }
            finally
            {
                if (completed)
                {
                    document.Close();
                }
                else
                {
                    // The output is thrown away; a failure while writing it must not hide the refusal.
                    try { document.Close(); } catch (Exception) { }
                }
            }
        }

        var report = new Report(flattened, removed.ToList());
        return new Result(report.Changed ? output.ToArray() : source, report);
    }

    // ── form ─────────────────────────────────────────────────────────────

    private static int FlattenForm(PdfDocument document, PdfDictionary catalog, PdfAcroForm form, ISet<string> removed)
    {
        var terminals = TerminalFields(form);
        var toFlatten = terminals.Where(f => !PdfName.Sig.Equals(f.Field.GetFormType())).ToList();

        if (form.HasXfaForm() || catalog.ContainsKey(new PdfName("NeedsRendering")))
        {
            // Dynamic XFA is drawn by the viewer from the XFA template; the AcroForm part (if any)
            // is a stub. There is nothing faithful to flatten.
            var needsRendering = catalog.GetAsBoolean(new PdfName("NeedsRendering"));
            var hasWidgets = terminals.Any(f => f.Field.GetWidgets().Count > 0);
            if (needsRendering?.GetValue() == true || (form.HasXfaForm() && !hasWidgets))
            {
                throw new ApiValidationException(XfaDynamicCode, "dynamic XFA form");
            }
            if (form.HasXfaForm())
            {
                form.RemoveXfaForm();
                removed.Add(RemovedKinds.Xfa);
            }
            catalog.Remove(new PdfName("NeedsRendering"));
        }

        var needAppearances = form.GetNeedAppearances()?.GetValue() == true;
        PdfFont? unicodeFont = null;
        PdfFont UnicodeFont() => unicodeFont ??= AppFonts.Load(AppFontFamily.Sans);

        foreach (var (_, field) in toFlatten)
        {
            var widgets = field.GetChildFormAnnotations();
            var type = field.GetFormType();
            // The appearance a viewer shows: with NeedAppearances it draws /V itself, and a widget
            // without /AP gets one generated. Either way we regenerate — with a font that can draw
            // the value (the default Helvetica has no code for č, ř, ů).
            if ((PdfName.Tx.Equals(type) || PdfName.Ch.Equals(type))
                && (needAppearances || widgets.Any(w => !IsHidden(document, w.GetPdfObject()) && !HasNormalAppearance(w.GetPdfObject()))))
            {
                var texts = DrawnTexts(field);
                // iText 9.8 draws combo options with PdfString.GetValue(): a UTF-16 option (any č)
                // comes out as its raw bytes. Options decoded for the drawing, put back after.
                var options = field.GetPdfObject().Get(PdfName.Opt);
                if (PdfName.Ch.Equals(type) && options is PdfArray optionArray)
                {
                    field.GetPdfObject().Put(PdfName.Opt, DecodedOptions(optionArray));
                }
                foreach (var widget in widgets)
                {
                    var merged = widget.GetPdfObject() == field.GetPdfObject();
                    // Each widget is drawn with its own /DA font and size: check that font, keep that size.
                    var font = merged ? field.GetFont() : widget.GetFont();
                    if (texts.All(t => CanDraw(font, t)))
                    {
                        widget.RegenerateField();
                    }
                    else if (merged)
                    {
                        field.SetFontAndSize(UnicodeFont(), field.GetFontSize());
                    }
                    else
                    {
                        // Its own /DA would put the old font back on regeneration.
                        var size = widget.GetFontSize();
                        widget.GetPdfObject().Remove(PdfName.DA);
                        widget.SetFontAndSize(UnicodeFont(), size);
                    }
                }
                if (options != null) field.GetPdfObject().Put(PdfName.Opt, options);
            }
            foreach (var widget in widgets)
            {
                // Not shown by a viewer, so not drawn: iText would draw it (or generate one).
                if (IsHidden(document, widget.GetPdfObject())) SetEmptyAppearance(document, widget.GetPdfObject());
            }
        }

        if (toFlatten.Count > 0)
        {
            if (toFlatten.Count < terminals.Count)
            {
                // Without a list iText flattens every field, empty signature fields included.
                foreach (var (name, _) in toFlatten)
                {
                    form.PartialFormFlattening(name);
                }
            }
            form.FlattenFields();
        }

        // What flattening could not take (a widget on no page) must not stay as a live field.
        var acroForm = catalog.GetAsDictionary(PdfName.AcroForm);
        if (acroForm != null)
        {
            var fields = acroForm.GetAsArray(PdfName.Fields);
            if (fields != null)
            {
                PruneNonSignatureFields(fields, inheritedType: null, depth: 0);
            }
            acroForm.Remove(PdfName.NeedAppearances);
            acroForm.Remove(PdfName.CO);
            if (fields == null || fields.IsEmpty())
            {
                catalog.Remove(PdfName.AcroForm);
            }
        }

        return toFlatten.Count;
    }

    /// <summary>Fields that hold a value (no child fields), with their fully qualified names.</summary>
    private static List<(string Name, PdfFormField Field)> TerminalFields(PdfAcroForm form) =>
        form.GetAllFormFields()
            .Where(kv => kv.Value.GetFormType() != null && kv.Value.GetChildFormFields().Count == 0)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

    /// <summary>Not shown by a viewer: the Hidden / NoView flag, or a layer that is off by default.</summary>
    private static bool IsHidden(PdfDocument document, PdfDictionary widget)
    {
        var flags = widget.GetAsNumber(PdfName.F)?.IntValue() ?? 0;
        if ((flags & (PdfAnnotation.HIDDEN | PdfAnnotation.NO_VIEW)) != 0) return true;
        var layer = widget.GetAsDictionary(PdfName.OC);
        var off = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.OCProperties)
            ?.GetAsDictionary(PdfName.D)?.GetAsArray(PdfName.OFF);
        return layer != null && off != null && off.Contains(layer);
    }

    /// <summary>
    /// Every text the appearance of a field can show: the value and, for a choice field, each
    /// option (a combo shows the display text of the selected export value, a list all of them).
    /// </summary>
    private static List<string> DrawnTexts(PdfFormField field)
    {
        var texts = new List<string> { field.GetValueAsString() ?? string.Empty };
        var options = field.GetPdfObject().GetAsArray(PdfName.Opt);
        if (options == null) return texts;
        for (var i = 0; i < options.Size(); i++)
        {
            switch (options.Get(i))
            {
                case PdfString option:
                    texts.Add(option.ToUnicodeString());
                    break;
                case PdfArray pair:
                    for (var j = 0; j < pair.Size(); j++)
                    {
                        if (pair.Get(j) is PdfString part) texts.Add(part.ToUnicodeString());
                    }
                    break;
            }
        }
        return texts;
    }

    private static PdfArray DecodedOptions(PdfArray options)
    {
        var decoded = new PdfArray();
        for (var i = 0; i < options.Size(); i++)
        {
            switch (options.Get(i))
            {
                case PdfString option:
                    decoded.Add(new PdfString(option.ToUnicodeString()));
                    break;
                case PdfArray pair:
                    var copy = new PdfArray();
                    for (var j = 0; j < pair.Size(); j++)
                    {
                        copy.Add(pair.Get(j) is PdfString part ? new PdfString(part.ToUnicodeString()) : pair.Get(j));
                    }
                    decoded.Add(copy);
                    break;
                default:
                    decoded.Add(options.Get(i));
                    break;
            }
        }
        return decoded;
    }

    /// <summary>
    /// A signature field with a value, /FT inherited from a parent included: SignatureUtil only
    /// counts fields with their own /FT and a complete signature dictionary.
    /// </summary>
    private static bool HasSignatureValue(PdfArray? fields, PdfName? inheritedType, int depth)
    {
        if (fields == null || depth > MaxDepth) return false;
        for (var i = 0; i < fields.Size(); i++)
        {
            var field = fields.GetAsDictionary(i);
            if (field == null) continue;
            var type = field.GetAsName(PdfName.FT) ?? inheritedType;
            if (PdfName.Sig.Equals(type) && field.Get(PdfName.V) is PdfDictionary) return true;
            if (HasSignatureValue(field.GetAsArray(PdfName.Kids), type, depth + 1)) return true;
        }
        return false;
    }

    private static bool HasNormalAppearance(PdfDictionary widget)
    {
        var normal = widget.GetAsDictionary(PdfName.AP)?.Get(PdfName.N);
        return normal is PdfStream || normal is PdfDictionary;
    }

    private static void SetEmptyAppearance(PdfDocument document, PdfDictionary widget)
    {
        var rect = widget.GetAsRectangle(PdfName.Rect) ?? new Rectangle(0, 0, 0, 0);
        var empty = new PdfFormXObject(new Rectangle(0, 0, Math.Abs(rect.GetWidth()), Math.Abs(rect.GetHeight())));
        empty.MakeIndirect(document);
        var appearance = new PdfDictionary();
        appearance.Put(PdfName.N, empty.GetPdfObject());
        widget.Put(PdfName.AP, appearance);
        widget.Remove(PdfName.AS);
    }

    /// <summary>
    /// Whether the font can show the value: it has the glyphs and its encoding round-trips the text
    /// (a standard Helvetica knows the ř glyph, but WinAnsi has no code for it).
    /// </summary>
    private static bool CanDraw(PdfFont font, string value)
    {
        var text = new string(value.Where(c => c >= 0x20).ToArray()); // line breaks of a multiline field
        for (var i = 0; i < text.Length; i++)
        {
            var codePoint = char.ConvertToUtf32(text, i);
            if (char.IsSurrogatePair(text, i)) i++;
            if (!font.ContainsGlyph(codePoint)) return false;
        }
        try
        {
            return font.Decode(new PdfString(font.ConvertToBytes(text))) == text;
        }
        catch (Exception ex) when (ex is PdfException || ex is ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Removes every terminal field that is not a signature field; parents left without kids go too.</summary>
    private static void PruneNonSignatureFields(PdfArray fields, PdfName? inheritedType, int depth)
    {
        for (var i = fields.Size() - 1; i >= 0; i--)
        {
            var field = fields.GetAsDictionary(i);
            if (field == null || depth > MaxDepth)
            {
                fields.Remove(i);
                continue;
            }
            var type = field.GetAsName(PdfName.FT) ?? inheritedType;
            var kids = field.GetAsArray(PdfName.Kids);
            var fieldKids = kids?.Size() > 0 && Enumerable.Range(0, kids.Size()).Any(k => kids.GetAsDictionary(k)?.ContainsKey(PdfName.T) == true);
            if (fieldKids)
            {
                PruneNonSignatureFields(kids!, type, depth + 1);
                if (kids!.IsEmpty()) fields.Remove(i);
                continue;
            }
            if (!PdfName.Sig.Equals(type))
            {
                fields.Remove(i);
            }
        }
    }

    // ── active content ───────────────────────────────────────────────────

    private static void RemoveActiveContent(PdfDocument document, PdfDictionary catalog, ISet<string> removed)
    {
        var seen = new HashSet<PdfIndirectReference>();

        var names = catalog.GetAsDictionary(PdfName.Names);
        if (names != null && names.Remove(PdfName.JavaScript) != null)
        {
            removed.Add(RemovedKinds.DocumentJavaScript);
        }

        if (catalog.Get(PdfName.OpenAction) is PdfDictionary)
        {
            if (CleanActionEntry(catalog, PdfName.OpenAction, seen)) removed.Add(RemovedKinds.OpenAction);
        }
        if (CleanAdditionalActions(catalog, seen)) removed.Add(RemovedKinds.DocumentActions);

        for (var p = 1; p <= document.GetNumberOfPages(); p++)
        {
            var page = document.GetPage(p).GetPdfObject();
            if (CleanAdditionalActions(page, seen)) removed.Add(RemovedKinds.PageActions);

            var annots = page.GetAsArray(PdfName.Annots);
            if (annots == null) continue;
            for (var a = 0; a < annots.Size(); a++)
            {
                var annot = annots.GetAsDictionary(a);
                if (annot == null) continue;
                var changed = CleanActionEntry(annot, PdfName.A, seen) | CleanAdditionalActions(annot, seen);
                if (changed)
                {
                    removed.Add(PdfName.Widget.Equals(annot.GetAsName(PdfName.Subtype)) ? RemovedKinds.FieldActions : RemovedKinds.AnnotationActions);
                }
            }
        }

        // Fields left after flattening (empty signature fields), with their kids.
        var fields = catalog.GetAsDictionary(PdfName.AcroForm)?.GetAsArray(PdfName.Fields);
        if (fields != null && CleanFields(fields, seen, depth: 0)) removed.Add(RemovedKinds.FieldActions);

        // Bookmarks: a click runs their action like a link's.
        var outlines = catalog.GetAsDictionary(PdfName.Outlines);
        if (outlines != null && CleanOutlines(outlines, seen, new HashSet<PdfDictionary>(), depth: 0)) removed.Add(RemovedKinds.OutlineActions);
    }

    private static bool CleanFields(PdfArray fields, HashSet<PdfIndirectReference> seen, int depth)
    {
        if (depth > MaxDepth) return false;
        var changed = false;
        for (var i = 0; i < fields.Size(); i++)
        {
            var field = fields.GetAsDictionary(i);
            if (field == null) continue;
            changed |= CleanActionEntry(field, PdfName.A, seen);
            changed |= CleanAdditionalActions(field, seen);
            var kids = field.GetAsArray(PdfName.Kids);
            if (kids != null) changed |= CleanFields(kids, seen, depth + 1);
        }
        return changed;
    }

    private static bool CleanOutlines(PdfDictionary node, HashSet<PdfIndirectReference> seen, HashSet<PdfDictionary> visited, int depth)
    {
        var changed = false;
        var item = node.GetAsDictionary(PdfName.First);
        while (item != null && visited.Add(item) && visited.Count < 100_000)
        {
            changed |= CleanActionEntry(item, PdfName.A, seen);
            if (depth < MaxDepth) changed |= CleanOutlines(item, seen, visited, depth + 1);
            item = item.GetAsDictionary(PdfName.Next);
        }
        return changed;
    }

    /// <summary>Cleans each trigger of an <c>/AA</c> dictionary; drops the dictionary once empty.</summary>
    private static bool CleanAdditionalActions(PdfDictionary owner, HashSet<PdfIndirectReference> seen)
    {
        var aa = owner.GetAsDictionary(PdfName.AA);
        if (aa == null) return false;
        var changed = false;
        foreach (var key in aa.KeySet().ToList())
        {
            changed |= CleanActionEntry(aa, key, seen);
        }
        if (aa.Size() == 0)
        {
            owner.Remove(PdfName.AA);
        }
        return changed;
    }

    /// <summary>Cleans the action under <paramref name="key"/>; removes the key when the action itself goes. True = something removed.</summary>
    private static bool CleanActionEntry(PdfDictionary owner, PdfName key, HashSet<PdfIndirectReference> seen)
    {
        if (owner.Get(key) is not PdfDictionary action) return false;
        var (keep, changed) = CleanAction(action, seen, depth: 0);
        if (!keep)
        {
            owner.Remove(key);
            return true;
        }
        return changed;
    }

    /// <summary>
    /// JavaScript or Launch: the action goes (Keep = false). Anything else stays, with its
    /// <c>/Next</c> chain cleaned the same way (a URI link that chains to JavaScript keeps the URI).
    /// </summary>
    private static (bool Keep, bool Changed) CleanAction(PdfDictionary action, HashSet<PdfIndirectReference> seen, int depth)
    {
        if (IsActive(action)) return (false, true);
        var reference = action.GetIndirectReference();
        if (reference != null && !seen.Add(reference)) return (true, false);
        if (depth > MaxDepth)
        {
            // A chain this deep is not a real document; cut it rather than follow it.
            return (true, action.Remove(PdfName.Next) != null);
        }

        var changed = false;
        var next = action.Get(PdfName.Next);
        if (next is PdfArray chain)
        {
            for (var i = chain.Size() - 1; i >= 0; i--)
            {
                if (chain.Get(i) is not PdfDictionary item) continue;
                var (keep, itemChanged) = CleanAction(item, seen, depth + 1);
                changed |= itemChanged;
                if (!keep) chain.Remove(i);
            }
            if (chain.IsEmpty()) action.Remove(PdfName.Next);
        }
        else if (next is PdfDictionary single)
        {
            var (keep, itemChanged) = CleanAction(single, seen, depth + 1);
            changed |= itemChanged;
            if (!keep) action.Remove(PdfName.Next);
        }
        return (true, changed);
    }

    private static bool IsActive(PdfDictionary action)
    {
        var kind = action.GetAsName(PdfName.S);
        return PdfName.Launch.Equals(kind) || PdfName.JavaScript.Equals(kind) || action.ContainsKey(PdfName.JS);
    }
}
