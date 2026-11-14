namespace Folio.Scripting.Rtc;

/// <summary>A decoded, cropped 8-bit 4:2:0 frame. Planes are tightly packed (strides equal the plane widths).</summary>
internal sealed record DecodedVideoFrame(int Width, int Height, byte[] Y, byte[] U, byte[] V)
{
    internal int ChromaWidth => Width / 2;
    internal int ChromaHeight => Height / 2;
}

/// <summary>The minimal contract a video codec exposes to the RTP media path: one access unit (or byte stream) in, zero or more frames out.</summary>
internal interface IVideoFrameDecoder
{
    /// <summary>Decodes an Annex-B byte stream (start-code delimited NAL units). Never throws on malformed input.</summary>
    IReadOnlyList<DecodedVideoFrame> Decode(ReadOnlySpan<byte> annexB);
}

/// <summary>
/// Resource limits for one decoder; the host derives these from its per-document media budget. They are host policy that
/// bounds memory and work, not an H.264 level conformance check: a stream within them may still exceed its declared level.
/// </summary>
internal sealed record H264DecoderLimits
{
    /// <summary>Largest accepted picture in macroblocks (8160 is 1920x1088).</summary>
    internal int MaxMacroblocks { get; init; } = 8160;

    /// <summary>Ceiling for all picture buffers (reference frames plus the picture being decoded) in bytes.</summary>
    internal long MaxMemoryBytes { get; init; } = 96L << 20;

    /// <summary>Largest input buffer passed to one <see cref="IVideoFrameDecoder.Decode"/> call; larger calls are dropped.</summary>
    internal int MaxInputBytes { get; init; } = 4 << 20;

    internal int MaxNalUnitsPerCall { get; init; } = 4096;

    /// <summary>Most frames one Decode call may return; the call stops early (like the NAL cap) once reached.</summary>
    internal int MaxFramesPerCall { get; init; } = 32;

    /// <summary>Most decoded-frame bytes one Decode call may return.</summary>
    internal long MaxOutputBytesPerCall { get; init; } = 64L << 20;

    /// <summary>
    /// Macroblock-work budget per Decode call: every started picture, decoded or skipped macroblock and every inserted
    /// frame_num-gap frame is charged in macroblocks. The call stops early when it is exhausted.
    /// </summary>
    internal long MaxWorkMacroblocksPerCall { get; init; } = 200_000;
}

/// <summary>
/// Managed ITU-T H.264 decoder for the Constrained Baseline subset: CAVLC, I and P slices, 4:2:0 8-bit, frame coding,
/// one slice group, multiple slices per picture, multiple reference frames with list modification and the sliding-window
/// and adaptive (MMCO) reference marking. CABAC, B slices, interlace, FMO/ASO, weighted prediction and the High
/// profiles are rejected: the affected NAL units are dropped and the decoder keeps running.
/// <para>Not thread-safe: use one instance per video stream and call it from one thread at a time. Each
/// <see cref="Decode"/> call must carry whole access units (the depacketizer emits exactly that): the picture in progress is
/// completed at the end of every call, so splitting one picture's slices across calls yields duplicate pictures.</para>
/// </summary>
internal sealed partial class H264Decoder : IVideoFrameDecoder, IDisposable
{
    private sealed class Sps
    {
        internal int Id, Profile, Level, Log2MaxFrameNum, PocType, Log2MaxPocLsb, MaxRefFrames, MbW, MbH;
        internal int CropL, CropR, CropT, CropB;
        internal bool DeltaPicOrderAlwaysZero, GapsAllowed;
        internal byte[] Raw = [];
        internal int MaxFrameNum => 1 << Log2MaxFrameNum;
    }

    private sealed class Pps
    {
        internal int Id, SpsId, NumRefIdxDefault, PicInitQp, ChromaQpOffset;
        internal bool BottomFieldPicOrder, DeblockControl, ConstrainedIntra, RedundantPicCnt;
    }

    private sealed class Picture(int id, int lumaBytes, int chromaBytes)
    {
        internal readonly int Id = id;
        internal readonly byte[] Y = new byte[lumaBytes], U = new byte[chromaBytes], V = new byte[chromaBytes];
        internal int FrameNum;
        internal bool ShortTerm, LongTerm, NonExisting;
        internal int LongTermIdx;
    }

    private struct Mmco { internal int Op, A, B; }

    private sealed class SliceHeader
    {
        internal int FirstMb, SliceType, PpsId, FrameNum, IdrPicId, PocLsb, DeltaPocBottom, DeltaPoc0, DeltaPoc1, RedundantPicCnt;
        internal int NumRefIdx, SliceQp, DisableDeblock, AlphaOffset, BetaOffset, NalRefIdc;
        internal bool Idr, NoOutputOfPriorPics, LongTermReference, AdaptiveMarking;
        internal readonly List<(int Idc, int Value)> Reorder = [];
        internal readonly List<Mmco> Mmcos = [];
        internal bool IsI => SliceType == 2;
    }

    private readonly H264DecoderLimits _limits;
    private readonly Sps?[] _spsTable = new Sps?[32];
    private readonly Pps?[] _ppsTable = new Pps?[256];
    private readonly List<DecodedVideoFrame> _output = [];
    private readonly List<Picture> _dpb = [];
    private readonly Stack<Picture> _pool = new();

    private Sps? _sps;
    private Pps? _pps;
    private int _nextPictureId;
    private bool _haveKeyframe;
    private int _prevRefFrameNum;
    private int _maxLongTermIdx = -1;

    private Picture? _cur;
    private SliceHeader? _curHeader;     // first slice of the picture being decoded: carries the marking syntax
    private SliceHeader? _lastHeader;    // previous slice, for first-slice-of-picture detection

    /// <summary>When set, exceptions other than <see cref="H264Exception"/> escape instead of resetting the decoder. Used by the fuzz tests.</summary>
    internal bool RethrowUnexpected { get; set; }

    internal int PicturesDecoded { get; private set; }
    internal int NalUnitsDropped { get; private set; }

    /// <summary>Times the catch-all in <see cref="Decode"/> fired and reset the decoder: each one is a decoder bug, never bad input.</summary>
    internal int UnexpectedErrors { get; private set; }

    /// <summary>Decode calls cut short by a per-call budget.</summary>
    internal int BudgetStops { get; private set; }

    private sealed class BudgetExceeded : Exception;
    private long _work, _outputBytes;

    private readonly RtcMediaBudget? _budget;
    private long _reserved;

    /// <param name="budget">When set, the memory the active SPS needs is reserved against the document's video budget and released on a size change or <see cref="Dispose"/>.</param>
    internal H264Decoder(H264DecoderLimits? limits = null, RtcMediaBudget? budget = null)
    {
        _limits = limits ?? new H264DecoderLimits();
        _budget = budget;
    }

    /// <summary>Releases the budget reservation and every buffer. The decoder must not be used afterwards.</summary>
    public void Dispose()
    {
        _cur = null; _curHeader = _lastHeader = null;
        _dpb.Clear(); _pool.Clear();
        _haveKeyframe = false; _sps = null;
        ReleaseReservation();
    }

    private void ReleaseReservation()
    {
        if (_reserved > 0) _budget?.ReleaseVideoBytes(_reserved);
        _reserved = 0;
    }

    public IReadOnlyList<DecodedVideoFrame> Decode(ReadOnlySpan<byte> annexB)
    {
        _output.Clear();
        _work = _outputBytes = 0;
        if (annexB.Length == 0 || annexB.Length > _limits.MaxInputBytes) return [];
        try
        {
            var nals = 0;
            var pos = FindStartCode(annexB, 0, out var prefix);
            while (pos >= 0)
            {
                var start = pos + prefix;
                var next = FindStartCode(annexB, start, out var nextPrefix);
                var end = next < 0 ? annexB.Length : next;
                while (end > start && annexB[end - 1] == 0) end--;   // trailing_zero_8bits and the zero of a 4-byte start code
                if (end > start)
                {
                    if (++nals > _limits.MaxNalUnitsPerCall) break;
                    try { HandleNal(annexB.Slice(start, end - start)); }
                    catch (H264Exception) { NalUnitsDropped++; }
                    catch (BudgetExceeded) { BudgetStops++; AbandonPicture(); return _output.ToArray(); }
                }
                pos = next;
                prefix = nextPrefix;
            }
            FinishPicture();
        }
        catch (Exception e) when (!RethrowUnexpected && e is not OutOfMemoryException)
        {
            // Defence in depth: a decoder bug must not take the host down. Drop all state and wait for the next IDR.
            UnexpectedErrors++;
            Reset();
        }
        return _output.ToArray();
    }

    private void Charge(long macroblocks)
    {
        _work += macroblocks;
        if (_work > _limits.MaxWorkMacroblocksPerCall) throw new BudgetExceeded();
    }

    private void AbandonPicture()
    {
        if (_cur != null) Release(_cur);
        _cur = null;
        _curHeader = _lastHeader = null;
    }

    /// <summary>Drops reference state; decoding resumes at the next IDR picture.</summary>
    internal void Reset()
    {
        _cur = null;
        _curHeader = _lastHeader = null;
        _dpb.Clear();
        _haveKeyframe = false;
        _maxLongTermIdx = -1;
        _sps = null;
    }

    private static int FindStartCode(ReadOnlySpan<byte> d, int from, out int prefix)
    {
        for (var i = from; i + 2 < d.Length; i++)
        {
            if (d[i + 2] > 1) { i += 2; continue; }
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) { prefix = 3; return i; }
        }
        prefix = 3;
        return -1;
    }

    private void HandleNal(ReadOnlySpan<byte> nal)
    {
        if ((nal[0] & 0x80) != 0) throw new H264Exception("forbidden_zero_bit set");
        var refIdc = (nal[0] >> 5) & 3;
        var type = nal[0] & 0x1F;
        switch (type)
        {
            case 1 or 5:
                HandleSlice(nal, refIdc, type == 5);
                break;
            case 7:
                ParseSps(Unescape(nal[1..], out var n7), n7, nal);
                break;
            case 8:
                ParsePps(Unescape(nal[1..], out var n8), n8);
                break;
            case 9 or 10 or 11:
                FinishPicture();       // access unit delimiter / end of sequence: the previous picture is complete
                break;
        }
    }

    /// <summary>Rejects an Exp-Golomb value above <paramref name="max"/> before any arithmetic can overflow.</summary>
    private static int Bounded(int value, int max, string name) =>
        value > max ? throw new H264Exception(name + " out of range") : value;

    private static byte[] Unescape(ReadOnlySpan<byte> src, out int length)
    {
        var dst = new byte[src.Length];
        var n = 0;
        var zeros = 0;
        foreach (var b in src)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            dst[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        length = n;
        return dst;
    }

    // ---- parameter sets ----

    private void ParseSps(byte[] rbsp, int length, ReadOnlySpan<byte> rawNal)
    {
        var br = new H264BitReader(rbsp, length);
        var profile = br.U(8);
        br.U(8);
        var level = br.U(8);
        var id = br.Ue();
        if (id > 31) throw new H264Exception("sps id");
        var raw = rawNal.ToArray();
        var existing = _spsTable[id];
        if (existing != null && existing.Raw.AsSpan().SequenceEqual(raw)) return;   // repeated parameter sets are common in RTP
        _spsTable[id] = null;
        if (profile is not (66 or 77 or 88)) throw new H264Exception("unsupported profile " + profile);
        var s = new Sps { Id = id, Profile = profile, Level = level, Raw = raw };
        s.Log2MaxFrameNum = Bounded(br.Ue(), 12, "log2_max_frame_num_minus4") + 4;
        s.PocType = br.Ue();
        if (s.PocType > 2) throw new H264Exception("poc type");
        if (s.PocType == 0)
        {
            s.Log2MaxPocLsb = Bounded(br.Ue(), 12, "log2_max_pic_order_cnt_lsb_minus4") + 4;
        }
        else if (s.PocType == 1)
        {
            s.DeltaPicOrderAlwaysZero = br.U1() == 1;
            br.Se(); br.Se();
            var cycle = br.Ue();
            if (cycle > 255) throw new H264Exception("poc cycle");
            for (var i = 0; i < cycle; i++) br.Se();
        }
        s.MaxRefFrames = br.Ue();
        if (s.MaxRefFrames > 16) throw new H264Exception("max_num_ref_frames");
        s.GapsAllowed = br.U1() == 1;
        var w = Bounded(br.Ue(), 1023, "pic_width_in_mbs_minus1") + 1;
        var h = Bounded(br.Ue(), 1023, "pic_height_in_map_units_minus1") + 1;
        if ((long)w * h > _limits.MaxMacroblocks) throw new H264Exception("picture too large");
        s.MbW = w; s.MbH = h;
        if (br.U1() == 0) throw new H264Exception("interlaced coding is not supported");
        br.U1();   // direct_8x8_inference_flag
        if (br.U1() == 1)
        {
            s.CropL = br.Ue(); s.CropR = br.Ue(); s.CropT = br.Ue(); s.CropB = br.Ue();
            if (((long)s.CropL + s.CropR) * 2 >= w * 16L || ((long)s.CropT + s.CropB) * 2 >= h * 16L) throw new H264Exception("crop");
        }
        // VUI is not needed: Constrained Baseline output order equals decoding order.
        var frameBytes = (long)w * h * 384;
        if (frameBytes * (s.MaxRefFrames + 2) > _limits.MaxMemoryBytes) throw new H264Exception("exceeds memory budget");
        _spsTable[id] = s;
    }

    private void ParsePps(byte[] rbsp, int length)
    {
        var br = new H264BitReader(rbsp, length);
        var id = br.Ue();
        if (id > 255) throw new H264Exception("pps id");
        _ppsTable[id] = null;
        var p = new Pps { Id = id };
        p.SpsId = br.Ue();
        if (p.SpsId > 31) throw new H264Exception("sps id");
        if (br.U1() == 1) throw new H264Exception("CABAC is not supported");
        p.BottomFieldPicOrder = br.U1() == 1;
        if (br.Ue() != 0) throw new H264Exception("slice groups (FMO) are not supported");
        p.NumRefIdxDefault = Bounded(br.Ue(), 31, "num_ref_idx_l0_default_active_minus1") + 1;
        br.Ue();
        if (br.U1() == 1) throw new H264Exception("weighted prediction is not supported");
        br.U(2);
        var qp = 26 + br.Se();
        if (qp is < 0 or > 51) throw new H264Exception("pic_init_qp");
        p.PicInitQp = qp;
        br.Se();
        p.ChromaQpOffset = br.Se();
        if (p.ChromaQpOffset is < -12 or > 12) throw new H264Exception("chroma_qp_index_offset");
        p.DeblockControl = br.U1() == 1;
        p.ConstrainedIntra = br.U1() == 1;
        p.RedundantPicCnt = br.U1() == 1;
        _ppsTable[id] = p;
    }

    // ---- slices ----

    private void HandleSlice(ReadOnlySpan<byte> nal, int refIdc, bool idr)
    {
        var rbsp = Unescape(nal[1..], out var length);
        var br = new H264BitReader(rbsp, length);
        var h = ParseSliceHeader(br, refIdc, idr, out var sps, out var pps);
        if (h == null) return;

        if (_cur != null && IsNewPicture(_lastHeader!, h)) FinishPicture();
        if (_cur == null)
        {
            if (!StartPicture(h, sps, pps)) return;
        }
        _lastHeader = h;
        DecodeSliceData(h, br);
    }

    private SliceHeader? ParseSliceHeader(H264BitReader br, int refIdc, bool idr, out Sps sps, out Pps pps)
    {
        sps = null!; pps = null!;
        var h = new SliceHeader { NalRefIdc = refIdc, Idr = idr };
        h.FirstMb = br.Ue();
        var t = br.Ue();
        if (t > 9) throw new H264Exception("slice_type");
        h.SliceType = t % 5;
        h.PpsId = br.Ue();
        if (h.PpsId > 255 || _ppsTable[h.PpsId] is not { } p) throw new H264Exception("unknown pps");
        if (_spsTable[p.SpsId] is not { } s) throw new H264Exception("unknown sps");
        if (h.SliceType is not (0 or 2)) throw new H264Exception("only I and P slices are supported");
        pps = p; sps = s;
        h.FrameNum = br.U(s.Log2MaxFrameNum);
        if (idr)
        {
            h.IdrPicId = br.Ue();
            if (h.FrameNum != 0) throw new H264Exception("IDR frame_num");
        }
        if (s.PocType == 0)
        {
            h.PocLsb = br.U(s.Log2MaxPocLsb);
            if (p.BottomFieldPicOrder) h.DeltaPocBottom = br.Se();
        }
        else if (s.PocType == 1 && !s.DeltaPicOrderAlwaysZero)
        {
            h.DeltaPoc0 = br.Se();
            if (p.BottomFieldPicOrder) h.DeltaPoc1 = br.Se();
        }
        if (p.RedundantPicCnt)
        {
            h.RedundantPicCnt = br.Ue();
            if (h.RedundantPicCnt > 0) return null;   // redundant coded pictures are never needed without loss concealment
        }
        h.NumRefIdx = p.NumRefIdxDefault;
        if (h.SliceType == 0)
        {
            if (br.U1() == 1)
            {
                h.NumRefIdx = Bounded(br.Ue(), 15, "num_ref_idx_l0_active_minus1") + 1;
            }
            if (br.U1() == 1)
            {
                for (var i = 0; ; i++)
                {
                    var idc = br.Ue();
                    if (idc == 3) break;
                    if (idc > 2 || i > 32) throw new H264Exception("ref_pic_list_modification");
                    h.Reorder.Add((idc, br.Ue()));
                }
            }
        }
        if (refIdc != 0)
        {
            if (idr)
            {
                h.NoOutputOfPriorPics = br.U1() == 1;
                h.LongTermReference = br.U1() == 1;
            }
            else if (br.U1() == 1)
            {
                h.AdaptiveMarking = true;
                for (var i = 0; ; i++)
                {
                    var op = br.Ue();
                    if (op == 0) break;
                    if (op > 6 || i > 66) throw new H264Exception("mmco");
                    var m = new Mmco { Op = op };
                    if (op is 1 or 3) m.A = br.Ue();
                    if (op == 2) m.A = br.Ue();
                    if (op == 3) m.B = br.Ue();
                    if (op == 6) m.B = br.Ue();
                    if (op == 4) m.A = br.Ue();
                    h.Mmcos.Add(m);
                }
            }
        }
        var qpDelta = br.Se();
        h.SliceQp = p.PicInitQp + qpDelta;
        if (h.SliceQp is < 0 or > 51) throw new H264Exception("slice_qp");
        if (p.DeblockControl)
        {
            h.DisableDeblock = br.Ue();
            if (h.DisableDeblock > 2) throw new H264Exception("disable_deblocking_filter_idc");
            if (h.DisableDeblock != 1)
            {
                h.AlphaOffset = br.Se() * 2;
                h.BetaOffset = br.Se() * 2;
                if (h.AlphaOffset is < -12 or > 12 || h.BetaOffset is < -12 or > 12) throw new H264Exception("deblocking offsets");
            }
        }
        return h;
    }

    /// <summary>First-VCL-NAL-of-a-picture detection (7.4.1.2.4).</summary>
    private static bool IsNewPicture(SliceHeader a, SliceHeader b) =>
        a.FrameNum != b.FrameNum || a.PpsId != b.PpsId || (a.NalRefIdc == 0) != (b.NalRefIdc == 0) || a.Idr != b.Idr
        || (a.Idr && a.IdrPicId != b.IdrPicId) || a.PocLsb != b.PocLsb || a.DeltaPocBottom != b.DeltaPocBottom
        || a.DeltaPoc0 != b.DeltaPoc0 || a.DeltaPoc1 != b.DeltaPoc1;

    private bool StartPicture(SliceHeader h, Sps sps, Pps pps)
    {
        if (h.Idr)
        {
            if (!ReferenceEquals(_sps, sps)) ActivateSps(sps);
            _dpb.Clear();
            _prevRefFrameNum = 0;
            _maxLongTermIdx = -1;
            _haveKeyframe = true;
        }
        else
        {
            if (!_haveKeyframe) return false;     // nothing to predict from yet
            if (!ReferenceEquals(_sps, sps)) throw new H264Exception("parameter set changed outside an IDR picture");
            HandleFrameNumGap(h);
        }
        _pps = pps;
        var frameBytes = (long)_mbCount * 384;
        if (_output.Count + 1 > _limits.MaxFramesPerCall || _outputBytes + frameBytes > _limits.MaxOutputBytesPerCall) throw new BudgetExceeded();
        Charge(_mbCount);
        _cur = AcquirePicture();
        _cur.FrameNum = h.FrameNum;
        _cur.ShortTerm = _cur.LongTerm = _cur.NonExisting = false;
        _curHeader = h;
        BeginPictureState();
        return true;
    }

    private void ActivateSps(Sps sps)
    {
        _pool.Clear();
        _dpb.Clear();
        ReleaseReservation();
        if (_budget is not null)
        {
            // Reference pictures, the pool of recycled ones and the current one (6 over the reference count), the per-macroblock
            // state, up to three output frames in flight (4:2:0) and the RGBA conversion of the newest.
            var total = (long)sps.MbW * sps.MbH * (384L * (sps.MaxRefFrames + 6) + 3 * 384 + 1024);
            if (!_budget.TryReserveVideoBytes(total))
            {
                _sps = null; _haveKeyframe = false;
                throw new H264Exception("exceeds the document video budget");
            }
            _reserved = total;
        }
        _sps = sps;
        _mbW = sps.MbW; _mbH = sps.MbH;
        AllocatePictureState();
    }

    private Picture AcquirePicture()
    {
        if (_pool.Count > 0) return _pool.Pop();
        return new Picture(_nextPictureId++, _mbW * _mbH * 256, _mbW * _mbH * 64);
    }

    private void Release(Picture p)
    {
        if (_pool.Count < 4) _pool.Push(p);
    }

    // ---- frame_num gaps (8.2.5.2) ----

    private void HandleFrameNumGap(SliceHeader h)
    {
        var sps = _sps!;
        var max = sps.MaxFrameNum;
        if (h.FrameNum == _prevRefFrameNum) return;
        var expected = (_prevRefFrameNum + 1) % max;
        if (h.FrameNum == expected) return;
        // Frames were lost (or the stream uses gaps deliberately). Insert "non-existing" frames, at most as many as could
        // still be referenced, so that frame_num arithmetic and the sliding window stay coherent.
        var missing = (h.FrameNum - expected + max) % max;
        var keep = Math.Max(1, sps.MaxRefFrames);
        if (missing > keep) expected = (h.FrameNum - keep + max) % max;
        var template = LatestShortTerm();
        while (expected != h.FrameNum)
        {
            Charge(_mbCount);
            var p = AcquirePicture();
            p.FrameNum = expected;
            p.NonExisting = true;
            p.ShortTerm = true;
            p.LongTerm = false;
            if (template != null) { Buffer.BlockCopy(template.Y, 0, p.Y, 0, p.Y.Length); Buffer.BlockCopy(template.U, 0, p.U, 0, p.U.Length); Buffer.BlockCopy(template.V, 0, p.V, 0, p.V.Length); }
            else { Array.Fill(p.Y, (byte)128); Array.Fill(p.U, (byte)128); Array.Fill(p.V, (byte)128); }
            SlidingWindow(expected);
            _dpb.Add(p);
            _prevRefFrameNum = expected;
            expected = (expected + 1) % max;
        }
    }

    private Picture? LatestShortTerm()
    {
        Picture? best = null;
        var bestWrap = int.MinValue;
        foreach (var p in _dpb)
        {
            if (!p.ShortTerm) continue;
            var w = FrameNumWrap(p, _prevRefFrameNum);
            if (w > bestWrap) { bestWrap = w; best = p; }
        }
        return best ?? (_dpb.Count > 0 ? _dpb[^1] : null);
    }

    private int FrameNumWrap(Picture p, int currentFrameNum) => p.FrameNum > currentFrameNum ? p.FrameNum - _sps!.MaxFrameNum : p.FrameNum;

    // ---- reference marking (8.2.5) ----

    private void SlidingWindow(int currentFrameNum)
    {
        var shortCount = 0;
        foreach (var p in _dpb) if (p.ShortTerm) shortCount++;
        var longCount = _dpb.Count - shortCount;
        while (shortCount > 0 && shortCount + longCount >= Math.Max(1, _sps!.MaxRefFrames))
        {
            Picture? oldest = null;
            var oldestWrap = int.MaxValue;
            foreach (var p in _dpb)
            {
                if (!p.ShortTerm) continue;
                var w = FrameNumWrap(p, currentFrameNum);
                if (w < oldestWrap) { oldestWrap = w; oldest = p; }
            }
            _dpb.Remove(oldest!);
            Release(oldest!);
            shortCount--;
        }
    }

    private void MarkCurrent(SliceHeader h, Picture cur)
    {
        var sps = _sps!;
        if (h.Idr)
        {
            if (h.LongTermReference) { cur.LongTerm = true; cur.LongTermIdx = 0; _maxLongTermIdx = 0; }
            else { cur.ShortTerm = true; _maxLongTermIdx = -1; }
            _dpb.Add(cur);
            _prevRefFrameNum = 0;
            return;
        }
        var frameNum = h.FrameNum;
        var currentIsLong = false;
        var mmco5 = false;
        if (h.AdaptiveMarking)
        {
            foreach (var m in h.Mmcos)
            {
                switch (m.Op)
                {
                    case 1:
                        {
                            var pic = m.A < sps.MaxFrameNum ? FindShort(frameNum - (m.A + 1)) : null;
                            if (pic != null) Remove(pic);
                            break;
                        }
                    case 2:
                        {
                            var pic = _dpb.Find(p => p.LongTerm && p.LongTermIdx == m.A);
                            if (pic != null) Remove(pic);
                            break;
                        }
                    case 3:
                        {
                            var pic = m.A < sps.MaxFrameNum ? FindShort(frameNum - (m.A + 1)) : null;
                            if (pic == null || m.B > _maxLongTermIdx) break;
                            var holder = _dpb.Find(p => p.LongTerm && p.LongTermIdx == m.B);
                            if (holder != null) Remove(holder);
                            pic.ShortTerm = false; pic.LongTerm = true; pic.LongTermIdx = m.B;
                            break;
                        }
                    case 4:
                        _maxLongTermIdx = m.A - 1;
                        for (var i = _dpb.Count - 1; i >= 0; i--)
                            if (_dpb[i].LongTerm && _dpb[i].LongTermIdx > _maxLongTermIdx) Remove(_dpb[i]);
                        break;
                    case 5:
                        for (var i = _dpb.Count - 1; i >= 0; i--) Remove(_dpb[i]);
                        _maxLongTermIdx = -1;
                        mmco5 = true;
                        break;
                    case 6:
                        if (m.B > _maxLongTermIdx) break;
                        var other = _dpb.Find(p => p.LongTerm && p.LongTermIdx == m.B);
                        if (other != null) Remove(other);
                        cur.LongTerm = true; cur.LongTermIdx = m.B;
                        currentIsLong = true;
                        break;
                }
            }
        }
        else SlidingWindow(frameNum);
        if (!currentIsLong) cur.ShortTerm = true;
        if (mmco5) { cur.FrameNum = 0; frameNum = 0; }
        _dpb.Add(cur);
        // A conforming stream never overflows the window; a damaged one must not grow the buffer without bound.
        while (_dpb.Count > Math.Max(1, sps.MaxRefFrames))
        {
            Picture? oldest = null;
            var oldestWrap = int.MaxValue;
            foreach (var p in _dpb)
            {
                if (p == cur || !p.ShortTerm) continue;
                var w = FrameNumWrap(p, frameNum);
                if (w < oldestWrap) { oldestWrap = w; oldest = p; }
            }
            if (oldest == null) { oldest = _dpb.Find(p => p != cur && p.LongTerm); }
            if (oldest == null) break;
            Remove(oldest);
        }
        _prevRefFrameNum = frameNum;

        void Remove(Picture p) { _dpb.Remove(p); Release(p); }
    }

    private Picture? FindShort(int picNum)
    {
        foreach (var p in _dpb)
            if (p.ShortTerm && FrameNumWrap(p, _curHeader!.FrameNum) == picNum) return p;
        return null;
    }

    // ---- reference list construction (8.2.4) ----

    private Picture[] BuildRefList(SliceHeader h)
    {
        var sps = _sps!;
        var max = sps.MaxFrameNum;
        var shortRefs = _dpb.Where(p => p.ShortTerm).OrderByDescending(p => FrameNumWrap(p, h.FrameNum)).ToList();
        var longRefs = _dpb.Where(p => p.LongTerm).OrderBy(p => p.LongTermIdx).ToList();
        var initial = new List<Picture?>(shortRefs);
        initial.AddRange(longRefs);
        var n = h.NumRefIdx;
        var list = new Picture?[n + 1];
        for (var i = 0; i < n && i < initial.Count; i++) list[i] = initial[i];

        var picNumPred = h.FrameNum;
        var idx = 0;
        foreach (var (idc, value) in h.Reorder)
        {
            if (idx >= n) break;
            Picture? target;
            if (idc < 2)
            {
                if (value >= max) throw new H264Exception("abs_diff_pic_num");
                var diff = value + 1;
                int noWrap;
                if (idc == 0) { noWrap = picNumPred - diff; if (noWrap < 0) noWrap += max; }
                else { noWrap = picNumPred + diff; if (noWrap >= max) noWrap -= max; }
                picNumPred = noWrap;
                var picNum = noWrap > h.FrameNum ? noWrap - max : noWrap;
                target = null;
                foreach (var p in _dpb) if (p.ShortTerm && FrameNumWrap(p, h.FrameNum) == picNum) { target = p; break; }
            }
            else
            {
                target = _dpb.Find(p => p.LongTerm && p.LongTermIdx == value);
            }
            if (target == null) continue;   // reference to a picture that is gone: leave the list as is
            for (var c = n; c > idx; c--) list[c] = list[c - 1];
            list[idx++] = target;
            var w = idx;
            for (var c = idx; c <= n; c++)
                if (list[c] != target) list[w++] = list[c];
        }

        var result = new Picture[n];
        Picture? fallback = null;
        for (var i = 0; i < n; i++) if (list[i] != null) { fallback = list[i]; break; }
        fallback ??= _dpb.Count > 0 ? _dpb[^1] : null;
        if (fallback == null) throw new H264Exception("no reference picture available");
        for (var i = 0; i < n; i++) result[i] = list[i] ?? fallback;   // concealment for entries the stream left empty
        return result;
    }

    // ---- picture completion ----

    private void FinishPicture()
    {
        var cur = _cur;
        if (cur == null) return;
        var h = _curHeader!;
        _cur = null;
        _lastHeader = null;
        ConcealMissing(cur);
        Deblock(cur);
        PicturesDecoded++;
        EmitFrame(cur);
        if (h.NalRefIdc != 0) MarkCurrent(h, cur);
        else Release(cur);
    }

    private void EmitFrame(Picture p)
    {
        var sps = _sps!;
        var stride = _mbW * 16;
        var w = _mbW * 16 - 2 * (sps.CropL + sps.CropR);
        var hgt = _mbH * 16 - 2 * (sps.CropT + sps.CropB);
        var y = new byte[w * hgt];
        for (var r = 0; r < hgt; r++) Buffer.BlockCopy(p.Y, (r + 2 * sps.CropT) * stride + 2 * sps.CropL, y, r * w, w);
        var cw = w / 2; var ch = hgt / 2; var cs = stride / 2;
        var u = new byte[cw * ch];
        var v = new byte[cw * ch];
        for (var r = 0; r < ch; r++)
        {
            var src = (r + sps.CropT) * cs + sps.CropL;
            Buffer.BlockCopy(p.U, src, u, r * cw, cw);
            Buffer.BlockCopy(p.V, src, v, r * cw, cw);
        }
        _output.Add(new DecodedVideoFrame(w, hgt, y, u, v));
        _outputBytes += y.Length + u.Length + v.Length;
    }
}
