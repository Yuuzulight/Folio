namespace Folio.Scripting;

/// <summary>
/// Folio-owned Chart.js-compatible bridge: a subset of Chart.js for bar and line charts on a Folio canvas.
/// Supported: dataset <c>backgroundColor</c>, <c>borderColor</c> and <c>borderWidth</c> (bars also per point), several
/// datasets (grouped bars, several lines), <c>options.plugins.legend</c> (<c>display</c>, <c>position</c> top or bottom),
/// <c>options.plugins.title</c> (<c>display</c>, <c>text</c>), and a linear y axis with round ticks, <c>beginAtZero</c>,
/// <c>min</c> and <c>max</c>. Everything else in a config is ignored without a diagnostic.
/// </summary>
internal static class ChartBindings
{
    public static void Install(IScriptEngine engine) => engine.EvaluateHostScript(Script, "folio:chart-stand-in");

    // The bridge keeps Chart.js' defaults (12px labels, 10px padding, 40x12 legend boxes, 80% category and 90% bar
    // widths). Pure helpers hang off Chart.__bridge so tests can reach them; a page has no use for them.
    private const string Script = """
        (function() {
            var DEFAULT = 'rgba(54,162,235,0.5)';
            var FONT = '12px Arial';

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
                var w = Number(pick(value, index));
                return isFinite(w) && w >= 0 && pick(value, index) !== null && pick(value, index) !== undefined ? w : fallback;
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
                var d = Math.min(decimals(step), 6);
                var s = Math.abs(value).toFixed(d);
                var parts = s.split('.');
                parts[0] = parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, ',');
                return (value < 0 ? '-' : '') + parts.join('.');
            }

            // Space taken by the title and the legend, in Chart.js' units. Title: one 12px bold line is 14.4px plus
            // 10px above and below. Legend: 12px boxes, one row is 10px padding, 12px item, 10px padding.
            function legendItems(datasets, type) {
                var items = [];
                for (var i = 0; i < datasets.length; i++) {
                    if (datasets[i] && typeof datasets[i].label === 'string' && datasets[i].label !== '') items.push(i);
                }
                return items;
            }
            function layout(config, width, measure) {
                var options = (config && config.options) || {};
                var plugins = options.plugins || {};
                var datasets = (config && config.data && config.data.datasets) || [];
                var out = { titleLines: [], titleHeight: 0, legend: null, legendHeight: 0, top: 0, bottom: 0 };
                var title = plugins.title;
                if (title && title.display === true && title.text !== undefined && title.text !== null && title.text !== '') {
                    out.titleLines = (Array.isArray(title.text) ? title.text : [title.text]).map(String);
                    out.titleHeight = out.titleLines.length * 14.4 + 20;
                    out.top += out.titleHeight;
                }
                var legend = plugins.legend;
                var items = legendItems(datasets);
                if (!(legend && legend.display === false) && items.length) {
                    var position = legend && legend.position === 'bottom' ? 'bottom' : 'top';
                    var rows = [], row = null, rowWidth = 0;
                    for (var k = 0; k < items.length; k++) {
                        var text = String(datasets[items[k]].label);
                        var itemWidth = 40 + 6 + measure(text);
                        if (!row || rowWidth + itemWidth + 20 > width) { row = []; rows.push(row); rowWidth = 0; }
                        row.push({ index: items[k], text: text, width: itemWidth });
                        rowWidth += itemWidth + 10;
                    }
                    out.legend = { position: position, rows: rows };
                    out.legendHeight = 10 + rows.length * 22;
                    if (position === 'bottom') out.bottom += out.legendHeight; else out.top += out.legendHeight;
                }
                return out;
            }

            function Chart(canvas, config) {
                var context = canvas.getContext('2d');
                var data = (config && config.data) || {};
                var datasets = data.datasets || [];
                var labels = data.labels || [];
                var options = (config && config.options) || {};
                var yScale = (options.scales && options.scales.y) || {};
                var isLine = config && config.type === 'line';
                var width = canvas.width || 300;
                var height = canvas.height || 150;
                canvas.style.height = '213px';

                context.clearRect(0, 0, width, height);
                context.font = FONT;
                var measure = function(text) { return context.measureText(text).width; };
                var frame = layout(config, width, measure);

                // Data range over every dataset.
                var count = labels.length;
                var rmin = Infinity, rmax = -Infinity;
                for (var d = 0; d < datasets.length; d++) {
                    var values = (datasets[d] && datasets[d].data) || [];
                    if (values.length > count) count = values.length;
                    for (var v = 0; v < values.length; v++) {
                        var n = values[v] === null || values[v] === undefined ? NaN : Number(values[v]);
                        if (isFinite(n)) { if (n < rmin) rmin = n; if (n > rmax) rmax = n; }
                    }
                }
                if (!isFinite(rmin)) { rmin = 0; rmax = 1; }
                var beginAtZero = typeof yScale.beginAtZero === 'boolean' ? yScale.beginAtZero : !isLine;
                if (beginAtZero) { if (rmin > 0) rmin = 0; if (rmax < 0) rmax = 0; }
                if (rmin === rmax) { rmax += 1; if (!beginAtZero) rmin -= 1; }

                var top = frame.top + 10;
                var bottom = 30 + frame.bottom;
                var plotHeight = height - top - bottom;
                var maxTicks = Math.min(11, Math.ceil(plotHeight / 14.4));
                var ticks = niceTicks(rmin, rmax, maxTicks, yScale.min, yScale.max);
                var scaleMin = ticks[0], scaleMax = ticks[ticks.length - 1];
                var step = ticks.length > 1 ? ticks[1] - ticks[0] : 1;
                var tickLabels = ticks.map(function(t) { return formatTick(t, step); });
                var labelWidth = 0;
                for (var q = 0; q < tickLabels.length; q++) labelWidth = Math.max(labelWidth, measure(tickLabels[q]));
                // Axis labels, 3px padding and 8px tick marks, plus what the canvas border and axis line take; 32 is the width
                // Chart.js gives labels like 0.5 on the corpus pages.
                var left = Math.max(32, Math.ceil(labelWidth) + 15);
                // Line points sit on the plot edges, so the last label needs room for its right half.
                var lastLabel = count ? String(labels[count - 1] !== undefined && labels[count - 1] !== null && labels[count - 1] !== '' ? labels[count - 1] : count - 1) : '';
                var right = isLine ? Math.ceil(measure(lastLabel) / 2) : 0;
                var plotWidth = width - left - right;
                var span = scaleMax - scaleMin || 1;
                var yOf = function(value) { return top + plotHeight - (value - scaleMin) / span * plotHeight; };
                var slot = plotWidth / (count || 1);

                // Title, then legend, above (or the legend below) the plot.
                context.textBaseline = 'middle';
                if (frame.titleLines.length) {
                    context.font = 'bold ' + FONT;
                    context.fillStyle = '#666';
                    context.textAlign = 'center';
                    for (var tl = 0; tl < frame.titleLines.length; tl++)
                        context.fillText(frame.titleLines[tl], width / 2, 10 + 7.2 + tl * 14.4);
                    context.font = FONT;
                }
                if (frame.legend) {
                    var legendTop = frame.legend.position === 'bottom' ? height - frame.legendHeight : frame.titleHeight;
                    for (var r = 0; r < frame.legend.rows.length; r++) {
                        var row = frame.legend.rows[r], total = -10;
                        for (var w = 0; w < row.length; w++) total += row[w].width + 10;
                        var x = (width - total) / 2, y = legendTop + 10 + r * 22 + 6;
                        for (var e = 0; e < row.length; e++) {
                            var ds = datasets[row[e].index];
                            var bw = widthAt(ds.borderWidth, 0, isLine ? 3 : 0);
                            context.fillStyle = colourAt(context, ds.backgroundColor, 0, DEFAULT);
                            context.fillRect(x, y - 6, 40, 12);
                            if (bw > 0) {
                                context.strokeStyle = colourAt(context, ds.borderColor, 0, DEFAULT);
                                context.lineWidth = bw;
                                context.strokeRect(x + bw / 2, y - 6 + bw / 2, 40 - bw, 12 - bw);
                            }
                            context.fillStyle = '#666';
                            context.textAlign = 'left';
                            context.fillText(row[e].text, x + 46, y);
                            x += row[e].width + 10;
                        }
                    }
                }

                // Grid and y axis labels.
                context.strokeStyle = 'rgba(120,120,120,0.15)';
                context.lineWidth = 1;
                context.textAlign = 'right';
                for (var t = 0; t < ticks.length; t++) {
                    var gy = yOf(ticks[t]);
                    context.beginPath();
                    context.moveTo(left, gy);
                    context.lineTo(left + plotWidth, gy);
                    context.stroke();
                    context.fillStyle = '#666';
                    context.fillText(tickLabels[t], left - 4, gy);
                }
                // Bars sit between category lines; line points sit on them, first and last at the plot edges.
                var pointX = function(i) {
                    return isLine ? (count > 1 ? left + i * plotWidth / (count - 1) : left + plotWidth / 2) : left + (i + 0.5) * slot;
                };
                var gridLines = isLine ? count : count + 1;
                for (var g = 0; g < gridLines; g++) {
                    var gx = isLine ? pointX(g) : left + g * slot;
                    context.beginPath();
                    context.moveTo(gx, top);
                    context.lineTo(gx, top + plotHeight);
                    context.stroke();
                }

                context.textAlign = 'center';
                context.textBaseline = 'top';
                context.fillStyle = '#666';
                for (var j = 0; j < count; j++)
                    context.fillText(labels[j] !== undefined && labels[j] !== null && labels[j] !== '' ? String(labels[j]) : String(j), pointX(j), top + plotHeight + 8);

                var baseY = yOf(Math.min(Math.max(0, scaleMin), scaleMax));
                for (var s = 0; s < datasets.length; s++) {
                    var set = datasets[s] || {};
                    var points = set.data || [];
                    if (isLine) {
                        var lineColour = colourAt(context, set.borderColor, 0, DEFAULT);
                        var fillColour = colourAt(context, set.backgroundColor, 0, DEFAULT);
                        var lineWidth = widthAt(set.borderWidth, 0, 3);
                        context.strokeStyle = lineColour;
                        context.lineWidth = lineWidth;
                        context.lineJoin = 'round';
                        var pen = false;
                        context.beginPath();
                        for (var p = 0; p < points.length; p++) {
                            var pv = points[p] === null || points[p] === undefined ? NaN : Number(points[p]);
                            if (!isFinite(pv)) { pen = false; continue; }
                            if (pen) context.lineTo(pointX(p), yOf(pv)); else context.moveTo(pointX(p), yOf(pv));
                            pen = true;
                        }
                        if (lineWidth > 0) context.stroke();
                        context.lineWidth = 1;
                        for (var m = 0; m < points.length; m++) {
                            var mv = points[m] === null || points[m] === undefined ? NaN : Number(points[m]);
                            if (!isFinite(mv)) continue;
                            context.beginPath();
                            context.arc(pointX(m), yOf(mv), 3, 0, Math.PI * 2);
                            context.fillStyle = fillColour;
                            context.fill();
                            context.strokeStyle = lineColour;
                            context.stroke();
                        }
                    } else {
                        // Chart.js: 80% of the slot is shared by the datasets, each bar fills 90% of its share.
                        var share = slot * 0.8 / datasets.length;
                        for (var b = 0; b < points.length; b++) {
                            var bv = points[b] === null || points[b] === undefined ? NaN : Number(points[b]);
                            if (!isFinite(bv)) continue;
                            var bx = left + b * slot + slot * 0.1 + s * share + share * 0.05;
                            var bwid = share * 0.9;
                            var by = yOf(Math.min(Math.max(bv, scaleMin), scaleMax));
                            var bTop = Math.min(by, baseY), bHeight = Math.abs(by - baseY);
                            context.fillStyle = colourAt(context, set.backgroundColor, b, DEFAULT);
                            context.fillRect(bx, bTop, bwid, bHeight);
                            var borderWidth = widthAt(set.borderWidth, b, 0);
                            if (borderWidth > 0) {
                                context.strokeStyle = colourAt(context, set.borderColor, b, DEFAULT);
                                context.lineWidth = borderWidth;
                                context.strokeRect(bx + borderWidth / 2, bTop + borderWidth / 2, bwid - borderWidth, bHeight - borderWidth);
                            }
                        }
                    }
                }
                return { destroy: function() {} };
            }
            Object.defineProperty(Chart, '__bridge', {
                value: { validColour: validColour, colourAt: colourAt, niceTicks: niceTicks, formatTick: formatTick, layout: layout },
            });
            globalThis.Chart = Chart;
        })();
        """;
}
