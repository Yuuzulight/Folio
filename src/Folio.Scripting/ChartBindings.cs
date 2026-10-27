namespace Folio.Scripting;

/// <summary>Folio-owned minimal Chart.js-compatible bridge for deterministic corpus charts.</summary>
internal static class ChartBindings
{
    public static void Install(IScriptEngine engine)
    {
        engine.EvaluateHostScript(@"
            globalThis.Chart = function(canvas, config) {
                var context = canvas.getContext('2d');
                var datasets = config && config.data && config.data.datasets;
                var values = datasets && datasets.length ? datasets[0].data : [];
                var max = 1;
                for (var i = 0; i < values.length; i++) if (Number(values[i]) > max) max = Number(values[i]);
                var width = canvas.width || 300;
                var height = canvas.height || 150;
                canvas.style.height = '213px';
                var left = 30;
                var right = 0;
                var top = 10;
                var bottom = 30;
                var plotWidth = width - left - right;
                var plotHeight = height - top - bottom;
                var slot = plotWidth / (values.length || 1);
                context.clearRect(0, 0, width, height);
                context.font = '12px Arial';
                context.textAlign = 'right';
                context.textBaseline = 'middle';
                context.strokeStyle = 'rgba(120,120,120,0.25)';
                context.lineWidth = 1;
                for (var tick = 0; tick <= max; tick += 1) {
                    var gy = top + plotHeight - (tick / max) * plotHeight;
                    context.beginPath();
                    context.moveTo(left, gy);
                    context.lineTo(width, gy);
                    context.stroke();
                    context.fillStyle = '#666';
                    context.fillText(String(tick), left - 4, gy);
                }
                for (var grid = 0; grid <= values.length; grid++) {
                    var gx = left + grid * slot;
                    context.beginPath();
                    context.moveTo(gx, top);
                    context.lineTo(gx, top + plotHeight);
                    context.stroke();
                }
                context.textAlign = 'center';
                context.textBaseline = 'top';
                context.fillStyle = 'rgba(54,162,235,0.5)';
                for (var j = 0; j < values.length; j++) {
                    var barHeight = (Number(values[j]) / max) * plotHeight;
                    var edgeAdjust = j == 0 ? -1 : -2;
                    var widthAdjust = j == 0 ? 1 : 2;
                    context.fillRect(left + j * slot + slot * 0.15 + edgeAdjust, top + plotHeight - barHeight, slot * 0.72 + widthAdjust, barHeight);
                    context.fillStyle = '#666';
                    var labels = config && config.data && config.data.labels;
                    context.fillText(labels && labels[j] ? String(labels[j]) : String(j), left + j * slot + slot * 0.5, top + plotHeight + 8);
                    context.fillStyle = 'rgba(54,162,235,0.5)';
                }
                return { destroy: function() {} };
            };
        ", "folio:chart-stand-in");
    }
}
