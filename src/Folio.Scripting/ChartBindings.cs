namespace Folio.Scripting;

/// <summary>
/// Folio-owned Chart.js-compatible bridge: a subset of Chart.js 4 that draws bar, line, pie and doughnut charts on a
/// Folio canvas. Layout follows Chart.js' own numbers (legend, title, axis and tick sizes, tick thinning), checked
/// against the real library on the compatibility engine. A chart type outside that set draws nothing and is reported
/// through <see cref="ReportScript"/>. Options this bridge does not read are ignored without a diagnostic.
/// </summary>
internal static class ChartBindings
{
    public static void Install(IScriptEngine engine) => engine.EvaluateHostScript(Script, "folio:chart-stand-in");

    /// <summary>
    /// A script that returns, and clears, the messages the bridge queued for charts it could not draw (one per line), or an
    /// empty string. <see cref="ScriptRunner"/> turns them into diagnostics.
    /// </summary>
    public const string ReportScript =
        "(function(){var r=globalThis.__folioChartReports;if(!r||!r.length)return '';globalThis.__folioChartReports=[];return r.join('\\n');})()";

    // Pure helpers hang off Chart.__bridge so tests can reach them; a page has no use for them.
    private const string Script = """
        (function() {
            // Chart.js' Colors plugin palette, used when no dataset sets a colour.
            var PALETTE = [[54,162,235],[255,99,132],[255,159,64],[255,205,86],[75,192,192],[153,102,255],[201,203,207]];
            var TEXT = '#666', GRID = 'rgba(0,0,0,0.1)', DEFAULT = 'rgba(0,0,0,0.1)';
            var LINE_HEIGHT = 1.2, TICK_LENGTH = 8, TICK_PAD = 3, AXIS_HEIGHT = 28.4, TOP_INSET = 10.2;

            function font(size, bold) { return (bold ? 'bold ' : '') + size + 'px Arial'; }
            function get(o) { for (var i = 1; i < arguments.length && o !== null && o !== undefined; i++) o = o[arguments[i]]; return o; }
            function num(v, fallback) {
                if (v === null || v === undefined || v === '' || typeof v === 'boolean') return fallback;
                var n = Number(v);
                return isFinite(n) ? n : fallback;
            }
            function snap(v) { return Math.floor(v) + 0.5; }
            function report(message) {
                (globalThis.__folioChartReports = globalThis.__folioChartReports || []).push(message);
            }

            // A colour the canvas accepts: the context itself is the CSS colour parser (hex, rgb(), rgba(), hsl(),
            // named). An unparsable value leaves fillStyle untouched, so it is probed against two different sentinels.
            function validColour(context, value) {
                if (typeof value !== 'string' || !value) return false;
                context.fillStyle = '#000001'; context.fillStyle = value; var first = context.fillStyle;
                context.fillStyle = '#000002'; context.fillStyle = value;
                return first === context.fillStyle;
            }
            // Chart.js "indexable" options: an array gives one value per point (wrapping), anything else is shared.
            function pick(value, index) {
                return Array.isArray(value) ? (value.length ? value[index % value.length] : undefined) : value;
            }
            function colourAt(context, value, index, fallback) {
                var c = pick(value, index);
                return validColour(context, c) ? c : fallback;
            }
            function widthAt(value, index, fallback) {
                return num(pick(value, index), fallback) < 0 ? fallback : num(pick(value, index), fallback);
            }

            function niceNum(range) {
                var exponent = Math.floor(Math.log(range) / Math.LN10);
                var fraction = range / Math.pow(10, exponent);
                var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
                return nice * Math.pow(10, exponent);
            }
            function decimals(x) {
                if (!isFinite(x)) return 0;
                var e = 1, p = 0;
                while (Math.round(x * e) / e !== x && p < 10) { e *= 10; p++; }
                return p;
            }
            // Chart.js' linear tick generation: round steps, scale bounds snapped to ticks unless min/max are given.
            function niceTicks(rmin, rmax, maxTicks, min, max) {
                var minDefined = typeof min === 'number' && isFinite(min);
                var maxDefined = typeof max === 'number' && isFinite(max);
                if (minDefined) rmin = min;
                if (maxDefined) rmax = max;
                if (rmin > rmax) { var t = rmin; rmin = rmax; rmax = t; }
                var maxSpaces = Math.max(maxTicks, 2) - 1;
                var spacing = niceNum((rmax - rmin) / maxSpaces);
                if (!(spacing > 1e-14)) return [rmin, rmax];
                var numSpaces = Math.ceil(rmax / spacing) - Math.floor(rmin / spacing);
                if (numSpaces > maxSpaces) spacing = niceNum(numSpaces * spacing / maxSpaces);
                var niceMin = minDefined ? min : Math.floor(rmin / spacing) * spacing;
                var niceMax = maxDefined ? max : Math.ceil(rmax / spacing) * spacing;
                var spaces = (niceMax - niceMin) / spacing;
                spaces = Math.abs(spaces - Math.round(spaces)) < spacing / 1000 ? Math.round(spaces) : Math.ceil(spaces);
                var factor = Math.pow(10, Math.max(decimals(spacing), decimals(niceMin)));
                var ticks = [];
                for (var j = 0; j < spaces; j++) ticks.push(Math.round((niceMin + j * spacing) * factor) / factor);
                ticks.push(Math.round(niceMax * factor) / factor);
                return ticks;
            }
            function formatTick(value, step) {
                if (value === 0) return '0';
                var d = Math.min(decimals(Math.round(step * 1e9) / 1e9), 6);
                var s = Math.abs(value).toFixed(d);
                var parts = s.split('.');
                parts[0] = parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, ',');
                return (value < 0 ? '-' : '') + parts.join('.');
            }
            // Chart.js' autoSkip: when there are more ticks than fit (one per label size), keep every n-th, from the first.
            function skipIndices(count, length, size, offset) {
                // A scale whose ticks sit on the plot edges (not centred in slots) fits one more label.
                var limit = Math.max(1, Math.floor(length / size + (offset ? 0 : 1)));
                var step = count > limit ? Math.ceil(count / limit) : 1;
                var kept = [];
                for (var i = 0; i < count; i += step) kept.push(i);
                return kept;
            }
            // suggestedMin/Max only widen the range; grace (a number, or a percentage of the range) widens both ends.
            function range(opts, rmin, rmax, beginAtZero) {
                var minHard = typeof opts.min === 'number', maxHard = typeof opts.max === 'number';
                if (!minHard && num(opts.suggestedMin, null) !== null && rmin > opts.suggestedMin) rmin = opts.suggestedMin;
                if (!maxHard && num(opts.suggestedMax, null) !== null && rmax < opts.suggestedMax) rmax = opts.suggestedMax;
                if (beginAtZero) { if (rmin > 0 && !minHard) rmin = 0; if (rmax < 0 && !maxHard) rmax = 0; }
                if (rmin === rmax) { rmax += 1; if (!beginAtZero) rmin -= 1; }
                var g = opts.grace;
                if (g !== undefined && g !== null && g !== 0 && g !== '') {
                    var change = typeof g === 'string' && g.charAt(g.length - 1) === '%' ? parseFloat(g) / 100 * (rmax - rmin) : num(g, 0);
                    if (!minHard && !(beginAtZero && rmin === 0)) rmin -= change;
                    if (!maxHard && !(beginAtZero && rmax === 0)) rmax += change;
                }
                return [rmin, rmax];
            }

            // Colours per dataset. With no colour set on any dataset Chart.js' Colors plugin picks from a palette; otherwise
            // its defaults apply (a faint grey, white borders on arcs).
            function resolveColours(context, config, type) {
                var datasets = get(config, 'data', 'datasets') || [];
                var plugin = get(config, 'options', 'plugins', 'colors');
                var auto = !(plugin && plugin.enabled === false);
                for (var i = 0; auto && i < datasets.length; i++)
                    if (datasets[i] && (datasets[i].backgroundColor !== undefined || datasets[i].borderColor !== undefined)) auto = false;
                var arc = type === 'pie' || type === 'doughnut';
                var count = (get(config, 'data', 'labels') || []).length;
                var out = [];
                for (var s = 0; s < datasets.length; s++) {
                    var ds = datasets[s] || {};
                    var c = PALETTE[s % PALETTE.length], bg, border;
                    if (auto && arc) {
                        var n = Math.max(count, (ds.data || []).length);
                        bg = [];
                        for (var k = 0; k < n; k++) { var p = PALETTE[k % PALETTE.length]; bg.push('rgb(' + p.join(',') + ')'); }
                        border = '#fff';
                    } else if (auto) {
                        bg = 'rgba(' + c.join(',') + ',0.5)';
                        border = 'rgb(' + c.join(',') + ')';
                    } else {
                        bg = ds.backgroundColor !== undefined ? ds.backgroundColor : DEFAULT;
                        border = ds.borderColor !== undefined ? ds.borderColor : (arc ? '#fff' : DEFAULT);
                    }
                    out.push({ bg: bg, border: border, borderWidth: widthAt(ds.borderWidth, 0, arc ? 2 : type === 'line' ? 3 : 0) });
                }
                return out;
            }
            function legendItems(context, config, type, colours) {
                var datasets = get(config, 'data', 'datasets') || [], items = [];
                if (type === 'pie' || type === 'doughnut') {
                    var labels = get(config, 'data', 'labels') || [];
                    var first = colours[0] || { bg: DEFAULT, border: '#fff', borderWidth: 2 };
                    for (var i = 0; i < labels.length; i++)
                        items.push({ text: String(labels[i]), fill: colourAt(context, first.bg, i, DEFAULT), stroke: colourAt(context, first.border, i, '#fff'), lineWidth: first.borderWidth });
                    return items;
                }
                for (var s = 0; s < datasets.length; s++)
                    items.push({ text: String(datasets[s] && datasets[s].label), fill: colourAt(context, colours[s].bg, 0, DEFAULT), stroke: colourAt(context, colours[s].border, 0, DEFAULT), lineWidth: colours[s].borderWidth,
                        dash: type === 'line' && Array.isArray(datasets[s] && datasets[s].borderDash) ? datasets[s].borderDash : null });
                return items;
            }

            // Title and legend boxes, in Chart.js' units: a title is its lines plus 10px above and below, a legend row is a
            // 12px item plus 10px padding (and 10px more around the block). Left and right legends are as wide as their widest item.
            function layout(config, items, width, height, measure) {
                var plugins = get(config, 'options', 'plugins') || {};
                var out = { title: null, legend: null, top: 0, bottom: 0, left: 0, right: 0 };
                var t = plugins.title;
                if (t && t.display === true && t.text !== undefined && t.text !== null && t.text !== '') {
                    var size = num(get(t, 'font', 'size'), 12), pad = num(t.padding, 10);
                    var lines = (Array.isArray(t.text) ? t.text : [t.text]).map(String);
                    var position = t.position === 'bottom' ? 'bottom' : 'top';
                    out.title = { lines: lines, size: size, padding: pad, height: lines.length * size * LINE_HEIGHT + 2 * pad, position: position,
                        align: t.align === 'start' || t.align === 'end' ? t.align : 'center', color: t.color };
                    out[position] += out.title.height;
                }
                var lg = plugins.legend, labels = (lg && lg.labels) || {};
                if (!(lg && lg.display === false) && items.length) {
                    var fs = num(get(labels, 'font', 'size'), 12), boxW = num(labels.boxWidth, 40), boxH = num(labels.boxHeight, fs), padding = num(labels.padding, 10);
                    var itemH = Math.max(boxH, fs);
                    var pos = lg && (lg.position === 'bottom' || lg.position === 'left' || lg.position === 'right') ? lg.position : 'top';
                    var legendFont = font(fs, false), entries = [];
                    for (var k = 0; k < items.length; k++)
                        entries.push({ index: k, text: items[k].text, fill: items[k].fill, stroke: items[k].stroke, lineWidth: items[k].lineWidth, dash: items[k].dash,
                            textWidth: measure(items[k].text, legendFont), width: boxW + fs / 2 + measure(items[k].text, legendFont) });
                    out.legend = { position: pos, boxWidth: boxW, boxHeight: boxH, fontSize: fs, padding: padding, itemHeight: itemH, entries: entries, color: labels.color };
                    if (pos === 'left' || pos === 'right') {
                        var widest = 0;
                        for (var a = 0; a < entries.length; a++) widest = Math.max(widest, entries[a].width);
                        out.legend.width = widest + 2 * padding;
                        out.legend.height = entries.length * itemH + (entries.length - 1) * padding;
                        out[pos] += out.legend.width;
                    } else {
                        var rows = [], row = null, rowWidth = 0;
                        for (var r = 0; r < entries.length; r++) {
                            if (!row || rowWidth + entries[r].width + 2 * padding > width) { row = []; rows.push(row); rowWidth = 0; }
                            row.push(entries[r]);
                            rowWidth += entries[r].width + padding;
                        }
                        out.legend.rows = rows;
                        out.legend.height = padding + rows.length * (itemH + padding);
                        out[pos] += out.legend.height;
                    }
                }
                return out;
            }

            function drawBoxes(context, box, width, height) {
                var title = box.title, lg = box.legend;
                if (title) {
                    context.font = font(title.size, true);
                    context.fillStyle = validColour(context, title.color) ? title.color : TEXT;
                    context.textBaseline = 'middle';
                    context.textAlign = title.align === 'start' ? 'left' : title.align === 'end' ? 'right' : 'center';
                    var x = title.align === 'start' ? 0 : title.align === 'end' ? width : width / 2;
                    var y0 = title.position === 'bottom' ? height - title.height : 0;
                    for (var i = 0; i < title.lines.length; i++)
                        context.fillText(title.lines[i], x, y0 + title.padding + title.size * LINE_HEIGHT * (i + 0.5));
                }
                if (!lg) return;
                var textColour = validColour(context, lg.color) ? lg.color : TEXT;
                var swatch = function(entry, x, cy) {
                    context.fillStyle = entry.fill;
                    context.fillRect(x, cy - lg.boxHeight / 2, lg.boxWidth, lg.boxHeight);
                    if (entry.lineWidth > 0) {
                        context.strokeStyle = entry.stroke;
                        context.lineWidth = entry.lineWidth;
                        if (entry.dash) context.setLineDash(entry.dash);
                        context.strokeRect(x, cy - lg.boxHeight / 2, lg.boxWidth, lg.boxHeight);
                        if (entry.dash) context.setLineDash([]);
                    }
                    context.font = font(lg.fontSize, false);
                    context.fillStyle = textColour;
                    context.textAlign = 'left';
                    context.textBaseline = 'middle';
                    context.fillText(entry.text, x + lg.boxWidth + lg.fontSize / 2, cy);
                };
                if (lg.rows) {
                    var outer = box.title && box.title.position === lg.position ? box.title.height : 0;
                    var top = lg.position === 'bottom' ? height - outer - lg.height : outer;
                    for (var r = 0; r < lg.rows.length; r++) {
                        var row = lg.rows[r], total = -lg.padding;
                        for (var w = 0; w < row.length; w++) total += row[w].width + lg.padding;
                        var x0 = (width - total) / 2, cy = top + lg.padding + r * (lg.itemHeight + lg.padding) + lg.itemHeight / 2;
                        for (var e = 0; e < row.length; e++) { swatch(row[e], x0, cy); x0 += row[e].width + lg.padding; }
                    }
                } else {
                    var left = lg.position === 'right' ? width - lg.width : 0;
                    var y = (height - lg.height) / 2;
                    for (var n = 0; n < lg.entries.length; n++)
                        swatch(lg.entries[n], left + lg.padding, y + n * (lg.itemHeight + lg.padding) + lg.itemHeight / 2);
                }
            }

            function numberAt(values, i) {
                var v = values[i];
                return v === null || v === undefined || v === '' ? NaN : Number(v);
            }

            function drawCartesian(context, config, type, box, width, height, measure, colours) {
                var options = config.options || {}, scales = options.scales || {};
                var datasets = get(config, 'data', 'datasets') || [], labels = get(config, 'data', 'labels') || [];
                var isLine = type === 'line', horizontal = !isLine && options.indexAxis === 'y';
                var vOpts = (horizontal ? scales.x : scales.y) || {}, cOpts = (horizontal ? scales.y : scales.x) || {};
                var showValue = vOpts.display !== false, showIndex = cOpts.display !== false;
                var stacked = vOpts.stacked === true || cOpts.stacked === true;
                var count = labels.length;
                for (var d = 0; d < datasets.length; d++) count = Math.max(count, ((datasets[d] && datasets[d].data) || []).length);

                // Value spans per dataset and point: [lo, hi] (a bar runs lo..hi; a line point sits at hi).
                var spans = [], pos = [], neg = [], rmin = Infinity, rmax = -Infinity;
                for (var s = 0; s < datasets.length; s++) {
                    var values = (datasets[s] && datasets[s].data) || [], row = [];
                    for (var i = 0; i < count; i++) {
                        var v = numberAt(values, i);
                        if (!isFinite(v)) { row.push(null); continue; }
                        var lo = 0, hi = v;
                        if (stacked) {
                            pos[i] = pos[i] || 0; neg[i] = neg[i] || 0;
                            if (v >= 0) { lo = pos[i]; hi = lo + v; pos[i] = hi; } else { lo = neg[i]; hi = lo + v; neg[i] = hi; }
                        }
                        row.push([lo, hi]);
                        var a = isLine ? hi : Math.min(lo, hi), b = isLine ? hi : Math.max(lo, hi);
                        if (a < rmin) rmin = a;
                        if (b > rmax) rmax = b;
                    }
                    spans.push(row);
                }
                if (!isFinite(rmin)) { rmin = 0; rmax = 1; }
                var beginAtZero = typeof vOpts.beginAtZero === 'boolean' ? vOpts.beginAtZero : !isLine;
                var bounds = range(vOpts, rmin, rmax, beginAtZero);

                var categoryLabel = function(i) { var l = labels[i]; return l !== undefined && l !== null && l !== '' ? String(l) : String(i); };
                var widest = 0;
                for (var q = 0; q < count; q++) widest = Math.max(widest, measure(categoryLabel(q), font(12, false)));
                var tickLabel = vOpts.ticks && typeof vOpts.ticks.callback === 'function' ? vOpts.ticks.callback : null;
                var lineH = 12 * LINE_HEIGHT;

                var plotLeft, plotRight, plotTop, plotBottom, ticks, step, drawn, valueLabels, minV, maxV;
                var solveValueAxis = function(length) {
                    var maxTicks = Math.min(11, Math.ceil(horizontal ? length / 40 : length / lineH));
                    ticks = niceTicks(bounds[0], bounds[1], maxTicks, vOpts.min, vOpts.max);
                    minV = ticks[0]; maxV = ticks[ticks.length - 1];
                    step = ticks.length > 1 ? Math.round((ticks[1] - ticks[0]) * 1e9) / 1e9 : 1;
                    valueLabels = ticks.map(function(t, idx) {
                        if (tickLabel) { try { var r = tickLabel(t, idx, ticks); if (r !== undefined && r !== null) return String(r); } catch (e) { } }
                        return formatTick(t, step);
                    });
                    var widthest = 0;
                    for (var z = 0; z < valueLabels.length; z++) widthest = Math.max(widthest, measure(valueLabels[z], font(12, false)));
                    var size = horizontal ? widthest + TICK_PAD + TICK_PAD : lineH + TICK_PAD;
                    drawn = skipIndices(ticks.length, length, size, false);
                };
                var valueWidth = function() {
                    var w = 0;
                    for (var z = 0; z < drawn.length; z++) w = Math.max(w, measure(valueLabels[drawn[z]], font(12, false)) + 3);
                    return w;
                };
                var axisHeight = showIndex && !horizontal || showValue && horizontal ? AXIS_HEIGHT : 0;
                var boxLeft = box.left, boxRight = box.right;
                var bottomEdge = height - box.bottom - axisHeight;

                if (!horizontal) {
                    // The top label of the value axis hangs 10.2px above the plot unless a legend or title already makes room, or
                    // the last label that is drawn sits lower than that.
                    plotTop = Math.max(box.top, showValue ? TOP_INSET : 0);
                    plotBottom = bottomEdge;
                    solveValueAxis(plotBottom - plotTop);
                    if (showValue && box.top < TOP_INSET) {
                        var gap = (maxV - ticks[drawn[drawn.length - 1]]) / ((maxV - minV) || 1) * (plotBottom - plotTop);
                        plotTop = Math.max(box.top, TOP_INSET - gap);
                        solveValueAxis(plotBottom - plotTop);
                    }
                    plotLeft = boxLeft + (showValue ? valueWidth() + TICK_LENGTH + TICK_PAD : 0);
                    var lastW = count ? measure(categoryLabel(count - 1), font(12, false)) + 3 : 0;
                    plotRight = width - boxRight - (isLine && showIndex ? lastW / 2 + 1.5 : 0);
                } else {
                    plotTop = box.top;
                    plotBottom = bottomEdge;
                    var catWidth = 0;
                    for (var c = 0; showIndex && c < count; c++) catWidth = Math.max(catWidth, measure(categoryLabel(c), font(12, false)) + 3);
                    plotLeft = boxLeft + (showIndex ? catWidth + TICK_LENGTH + TICK_PAD : 0);
                    plotRight = width - boxRight;
                    solveValueAxis(plotRight - plotLeft);
                    plotRight -= showValue ? (measure(valueLabels[drawn[drawn.length - 1]], font(12, false)) + 3) / 2 + 1.5 : 0;
                    solveValueAxis(plotRight - plotLeft);
                }
                var plotW = plotRight - plotLeft, plotH = plotBottom - plotTop;
                var span = (maxV - minV) || 1;
                // Position along the value axis, and across the index axis (bars: slot centres; lines: first to last point).
                var valuePx = function(v) { return horizontal ? plotLeft + (v - minV) / span * plotW : plotBottom - (v - minV) / span * plotH; };
                var indexLength = horizontal ? plotH : plotW;
                var slot = indexLength / (count || 1);
                var indexPx = function(i) {
                    var start = horizontal ? plotTop : plotLeft;
                    return isLine ? (count > 1 ? start + i * indexLength / (count - 1) : start + indexLength / 2) : start + (i + 0.5) * slot;
                };
                var point = function(i, v) { return horizontal ? [valuePx(v), indexPx(i)] : [indexPx(i), valuePx(v)]; };

                context.lineWidth = 1;
                context.strokeStyle = GRID;
                var line = function(x0, y0, x1, y1) { context.beginPath(); context.moveTo(x0, y0); context.lineTo(x1, y1); context.stroke(); };
                var gridValue = get(vOpts, 'grid', 'display') !== false, gridIndex = get(cOpts, 'grid', 'display') !== false;
                var keptIndex = skipIndices(count, indexLength, horizontal ? lineH + TICK_PAD : widest + 2 * TICK_PAD, !isLine);

                // Value axis: grid lines, tick marks and labels for the ticks that survived thinning.
                if (showValue) {
                    context.fillStyle = TEXT;
                    context.font = font(12, false);
                    context.textBaseline = 'middle';
                    for (var t = 0; t < drawn.length; t++) {
                        var tv = ticks[drawn[t]], p = valuePx(tv), label = valueLabels[drawn[t]];
                        if (horizontal) {
                            if (gridValue) line(snap(p), plotTop, snap(p), plotBottom);
                            line(snap(p), Math.floor(plotBottom) + 1, snap(p), plotBottom + TICK_LENGTH);
                            context.textAlign = 'center';
                            context.fillText(label, p, plotBottom + TICK_LENGTH + TICK_PAD + lineH / 2);
                        } else {
                            if (gridValue) line(Math.floor(plotLeft), snap(p), plotRight, snap(p));
                            line(plotLeft - TICK_LENGTH, snap(p), Math.floor(plotLeft), snap(p));
                            context.textAlign = 'right';
                            context.fillText(label, plotLeft - TICK_LENGTH - TICK_PAD, p);
                        }
                    }
                }
                // Index axis: grid at slot boundaries (bars) or points (lines), labels for the labels that survived thinning.
                if (showIndex) {
                    context.fillStyle = TEXT;
                    context.font = font(12, false);
                    context.textBaseline = 'middle';
                    var boundary = function(i) { var start = horizontal ? plotTop : plotLeft; return isLine ? indexPx(i) : start + i * slot; };
                    var lines = [];
                    for (var g = 0; g < keptIndex.length; g++) lines.push(boundary(keptIndex[g]));
                    if (!isLine) lines.push(boundary(count));
                    for (var gl = 0; gl < lines.length; gl++) {
                        var gp = snap(lines[gl]);
                        if (horizontal) { if (gridIndex) line(Math.floor(plotLeft), gp, plotRight, gp); line(plotLeft - TICK_LENGTH, gp, Math.floor(plotLeft), gp); }
                        else { if (gridIndex) line(gp, plotTop, gp, plotBottom); line(gp, Math.floor(plotBottom) + 1, gp, plotBottom + TICK_LENGTH); }
                    }
                    for (var k = 0; k < keptIndex.length; k++) {
                        var ip = indexPx(keptIndex[k]);
                        if (horizontal) { context.textAlign = 'right'; context.fillText(categoryLabel(keptIndex[k]), plotLeft - TICK_LENGTH - TICK_PAD, ip); }
                        else { context.textAlign = 'center'; context.fillText(categoryLabel(keptIndex[k]), ip, plotBottom + TICK_LENGTH + TICK_PAD + lineH / 2); }
                    }
                }
                // The two axis lines.
                context.strokeStyle = GRID;
                context.lineWidth = 1;
                if (showValue || showIndex) {
                    line(Math.floor(plotLeft), snap(plotBottom), plotRight + 0.5, snap(plotBottom));
                    line(snap(plotLeft), plotTop, snap(plotLeft), plotBottom + 1);
                }

                // Datasets.
                var base = valuePx(Math.min(Math.max(0, minV), maxV));
                var catPct = 0.8, barPct = 0.9;
                for (var cp = 0; cp < datasets.length; cp++) {
                    if (num(datasets[cp] && datasets[cp].categoryPercentage, null) !== null) { catPct = datasets[cp].categoryPercentage; break; }
                }
                var stackCount = stacked ? 1 : datasets.length;
                for (var ds = 0; ds < datasets.length; ds++) {
                    var set = datasets[ds] || {}, color = colours[ds], row2 = spans[ds];
                    if (isLine) drawLine(context, set, color, row2, point, base, plotLeft, plotTop, plotRight, plotBottom, horizontal, stacked);
                    else {
                        var group = slot * catPct / stackCount, bp = num(set.barPercentage, barPct);
                        var offset = stacked ? 0 : ds;
                        for (var b = 0; b < count; b++) {
                            if (!row2[b]) continue;
                            var lo = row2[b][0], hi = row2[b][1];
                            var start = (horizontal ? plotTop : plotLeft) + b * slot + slot * (1 - catPct) / 2 + offset * group + group * (1 - bp) / 2;
                            var size = group * bp;
                            var from = valuePx(Math.min(Math.max(lo, minV), maxV)), to = valuePx(Math.min(Math.max(hi, minV), maxV));
                            // Chart.js draws a bar that starts at the baseline half a pixel clear of it.
                            if (Math.abs(from - base) < 0.01 && to !== from) from += to > from ? 0.5 : -0.5;
                            var rx = horizontal ? Math.min(from, to) : start, ry = horizontal ? start : Math.min(from, to);
                            var rw = horizontal ? Math.abs(to - from) : size, rh = horizontal ? size : Math.abs(to - from);
                            var fill = colourAt(context, color.bg, b, DEFAULT);
                            var radius = Math.min(num(pick(set.borderRadius, b), 0), rw / 2, rh / 2);
                            var positive = hi >= lo;
                            context.fillStyle = fill;
                            if (radius > 0 && typeof context.roundRect === 'function') {
                                var radii = horizontal ? (positive ? [0, radius, radius, 0] : [radius, 0, 0, radius]) : (positive ? [radius, radius, 0, 0] : [0, 0, radius, radius]);
                                context.beginPath();
                                context.roundRect(rx, ry, rw, rh, radii);
                                context.fill();
                            } else context.fillRect(rx, ry, rw, rh);
                            var bw = widthAt(set.borderWidth, b, 0);
                            if (bw > 0) {
                                context.strokeStyle = colourAt(context, pick(set.borderColor, b), 0, DEFAULT);
                                context.lineWidth = bw;
                                context.strokeRect(rx + bw / 2, ry + bw / 2, rw - bw, rh - bw);
                            }
                        }
                    }
                }
            }

            // Cardinal spline control points as Chart.js computes them, for tension > 0.
            function controls(prev, cur, next, tension) {
                var d01 = Math.sqrt(Math.pow(cur[0] - prev[0], 2) + Math.pow(cur[1] - prev[1], 2));
                var d12 = Math.sqrt(Math.pow(next[0] - cur[0], 2) + Math.pow(next[1] - cur[1], 2));
                var s01 = d01 / (d01 + d12) || 0, s12 = d12 / (d01 + d12) || 0;
                var fa = tension * s01, fb = tension * s12;
                return [[cur[0] - fa * (next[0] - prev[0]), cur[1] - fa * (next[1] - prev[1])], [cur[0] + fb * (next[0] - prev[0]), cur[1] + fb * (next[1] - prev[1])]];
            }
            function drawLine(context, set, color, row, point, base, left, top, right, bottom, horizontal, stacked) {
                var lineColour = colourAt(context, pick(color.border, 0), 0, DEFAULT), fillColour = colourAt(context, pick(color.bg, 0), 0, DEFAULT);
                var lineWidth = widthAt(set.borderWidth, 0, 3), tension = Math.max(0, num(set.tension, 0));
                // Runs of consecutive points; a missing value ends a run.
                var runs = [], current = [];
                for (var i = 0; i < row.length; i++) {
                    if (!row[i]) { if (current.length) runs.push(current); current = []; continue; }
                    var p = point(i, row[i][1]);
                    current.push({ x: p[0], y: p[1], index: i, value: row[i][1] });
                }
                if (current.length) runs.push(current);
                var trace = function(run) {
                    context.moveTo(run[0].x, run[0].y);
                    for (var j = 1; j < run.length; j++) {
                        if (tension > 0) {
                            var a = run[j - 1], b = run[j];
                            var ca = controls(run[Math.max(j - 2, 0)] ? [run[Math.max(j - 2, 0)].x, run[Math.max(j - 2, 0)].y] : [a.x, a.y], [a.x, a.y], [b.x, b.y], tension)[1];
                            var cb = controls([a.x, a.y], [b.x, b.y], run[Math.min(j + 1, run.length - 1)] ? [run[Math.min(j + 1, run.length - 1)].x, run[Math.min(j + 1, run.length - 1)].y] : [b.x, b.y], tension)[0];
                            if (j === 1) ca = [a.x, a.y];
                            if (j === run.length - 1) cb = [b.x, b.y];
                            context.bezierCurveTo(ca[0], ca[1], cb[0], cb[1], b.x, b.y);
                        } else context.lineTo(run[j].x, run[j].y);
                    }
                };
                // fill: true / 'origin' fills down to the zero line (the axis edge when zero is off the scale).
                if (set.fill === true || set.fill === 'origin' || set.fill === 'start' || set.fill === 'end') {
                    context.fillStyle = fillColour;
                    for (var r = 0; r < runs.length; r++) {
                        var run = runs[r], last = run[run.length - 1], first = run[0];
                        context.beginPath();
                        trace(run);
                        if (horizontal) { context.lineTo(base, last.y); context.lineTo(base, first.y); } else { context.lineTo(last.x, base); context.lineTo(first.x, base); }
                        context.closePath();
                        context.fill();
                    }
                }
                if (lineWidth > 0) {
                    context.strokeStyle = lineColour;
                    context.lineWidth = lineWidth;
                    context.lineJoin = 'miter';
                    if (Array.isArray(set.borderDash) && set.borderDash.length) context.setLineDash(set.borderDash);
                    for (var r2 = 0; r2 < runs.length; r2++) { context.beginPath(); trace(runs[r2]); context.stroke(); }
                    if (Array.isArray(set.borderDash) && set.borderDash.length) context.setLineDash([]);
                }
                for (var r3 = 0; r3 < runs.length; r3++) {
                    for (var m = 0; m < runs[r3].length; m++) {
                        var pt = runs[r3][m], idx = pt.index, radius = num(pick(set.pointRadius, idx), 3);
                        if (!(radius > 0)) continue;
                        context.beginPath();
                        context.arc(pt.x, pt.y, radius, 0, Math.PI * 2);
                        context.fillStyle = colourAt(context, set.pointBackgroundColor, idx, fillColour);
                        context.fill();
                        var pbw = widthAt(set.pointBorderWidth, idx, 1);
                        if (pbw > 0) {
                            context.lineWidth = pbw;
                            context.strokeStyle = colourAt(context, set.pointBorderColor, idx, lineColour);
                            context.stroke();
                        }
                    }
                }
            }

            function drawRadial(context, config, type, box, width, height, colours) {
                var dataset = (get(config, 'data', 'datasets') || [])[0] || {}, values = dataset.data || [], color = colours[0] || { bg: DEFAULT, border: '#fff', borderWidth: 2 };
                var left = box.left, right = width - box.right, top = box.top, bottom = height - box.bottom;
                var bw = color.borderWidth, total = 0;
                for (var i = 0; i < values.length; i++) { var v = numberAt(values, i); if (isFinite(v) && v > 0) total += v; }
                var cutoutOption = get(config, 'options', 'cutout');
                if (cutoutOption === undefined) cutoutOption = type === 'doughnut' ? '50%' : 0;
                var cutout = typeof cutoutOption === 'string' && cutoutOption.charAt(cutoutOption.length - 1) === '%' ? parseFloat(cutoutOption) / 100 : 0;
                var radius = Math.max(0, (Math.min(right - left, bottom - top) - bw) / 2);
                if (typeof cutoutOption === 'number') cutout = radius > 0 ? Math.min(1, cutoutOption / radius) : 0;
                var inner = radius * cutout, cx = (left + right) / 2, cy = (top + bottom) / 2;
                if (!(total > 0) || !(radius > 0)) return;
                var angle = -Math.PI / 2;
                for (var j = 0; j < values.length; j++) {
                    var value = numberAt(values, j);
                    if (!isFinite(value) || value <= 0) continue;
                    var sweep = value / total * Math.PI * 2;
                    context.beginPath();
                    context.arc(cx, cy, radius, angle, angle + sweep);
                    if (inner > 0) context.arc(cx, cy, inner, angle + sweep, angle, true); else context.lineTo(cx, cy);
                    context.closePath();
                    context.fillStyle = colourAt(context, color.bg, j, DEFAULT);
                    context.fill();
                    if (bw > 0) {
                        context.lineWidth = bw;
                        context.lineJoin = 'bevel';
                        context.strokeStyle = colourAt(context, color.border, j, '#fff');
                        context.stroke();
                    }
                    angle += sweep;
                }
            }

            function Chart(canvas, config) {
                var context = canvas.getContext('2d');
                var type = config && config.type !== undefined ? String(config.type) : 'bar';
                if (type !== 'bar' && type !== 'line' && type !== 'pie' && type !== 'doughnut') {
                    report('Chart type "' + type + '" is not supported by Folio\'s native chart bridge (bar, line, pie and doughnut are); nothing was drawn.');
                    return { destroy: function() {} };
                }
                var width = canvas.width || 300;
                var height = canvas.height || 150;
                canvas.style.height = '213px';
                context.clearRect(0, 0, width, height);
                var measure = function(text, fontString) {
                    context.font = fontString || font(12, false);
                    var w = context.measureText(text).width;
                    context.font = font(12, false);
                    return w;
                };
                var colours = resolveColours(context, config, type);
                var box = layout(config, legendItems(context, config, type, colours), width, height, measure);
                drawBoxes(context, box, width, height);
                if (type === 'pie' || type === 'doughnut') drawRadial(context, config, type, box, width, height, colours);
                else drawCartesian(context, config, type, box, width, height, measure, colours);
                return { destroy: function() {} };
            }
            Object.defineProperty(Chart, '__bridge', {
                value: { validColour: validColour, colourAt: colourAt, niceTicks: niceTicks, formatTick: formatTick, skipIndices: skipIndices,
                    range: range, resolveColours: resolveColours, legendItems: legendItems, layout: layout },
            });
            globalThis.Chart = Chart;
        })();
        """;
}
