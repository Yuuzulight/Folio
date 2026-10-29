namespace Folio.Imaging;

/// <summary>Small Folio-owned baseline JPEG encoder used by software canvas export.</summary>
internal static class JpegEncoder
{
    private static readonly int[] ZigZag = [0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63];
    private static readonly int[] QY = [16,11,10,16,24,40,51,61,12,12,14,19,26,58,60,55,14,13,16,24,40,57,69,56,14,17,22,29,51,87,80,62,18,22,37,56,68,109,103,77,24,35,55,64,81,104,113,92,49,64,78,87,103,121,120,101,72,92,95,98,112,100,103,99];
    private static readonly int[] QC = [17,18,24,47,99,99,99,99,18,21,26,66,99,99,99,99,24,26,56,99,99,99,99,99,47,66,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99,99];
    private static readonly double[,] Cos = BuildCos();

    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, double quality)
    {
        quality = Math.Clamp(double.IsFinite(quality) ? quality : 0.92, 0, 1);
        var scale = quality < 0.5 ? 5000 / Math.Max(1, (int)Math.Round(quality * 100)) : 200 - (int)Math.Round(quality * 100);
        var qy = Scale(QY, scale); var qc = Scale(QC, scale);
        using var output = new MemoryStream();
        Marker(output, 0xD8);
        WriteApp0(output);
        WriteQuant(output, 0, qy); WriteQuant(output, 1, qc);
        WriteFrame(output, width, height);
        WriteHuffman(output, 0, false); WriteHuffman(output, 1, true);
        output.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x01, 0x02, 0x01, 0x03, 0x01, 0x00, 0x3F, 0x00]);
        var bits = new Bits(output); var previous = new int[3]; var block = new double[64];
        for (var by = 0; by < height; by += 8)
            for (var bx = 0; bx < width; bx += 8)
                for (var component = 0; component < 3; component++)
                {
                    block.AsSpan().Clear();
                    for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
                    {
                        var px = Math.Min(width - 1, bx + x); var py = Math.Min(height - 1, by + y);
                        var p = (py * width + px) * 4; var a = rgba[p + 3] / 255.0;
                        var r = rgba[p] * a + 255 * (1 - a); var g = rgba[p + 1] * a + 255 * (1 - a); var b = rgba[p + 2] * a + 255 * (1 - a);
                        block[y * 8 + x] = component == 0 ? 0.299 * r + 0.587 * g + 0.114 * b - 128 : (component == 1 ? -0.168736 * r - 0.331264 * g + 0.5 * b : 0.5 * r - 0.418688 * g - 0.081312 * b);
                    }
                    var quant = component == 0 ? qy : qc; var coeff = Dct(block, quant); var dc = coeff[0];
                    var diff = dc - previous[component]; previous[component] = dc; WriteValue(bits, diff, 0); var zeroes = 0;
                    for (var k = 1; k < 64; k++) { var value = coeff[ZigZag[k]]; if (value == 0) { zeroes++; continue; } while (zeroes >= 16) { bits.WriteByte(0xF0); zeroes -= 16; } WriteValue(bits, value, 1, zeroes); zeroes = 0; }
                    if (zeroes > 0) bits.WriteByte(0x00);
                }
        bits.Flush(); Marker(output, 0xD9); return output.ToArray();
    }

    private static int[] Scale(int[] source, int scale) => source.Select(v => Math.Clamp((v * scale + 50) / 100, 1, 255)).ToArray();
    private static int[] Dct(Span<double> input, int[] q)
    { var output = new int[64]; for (var v=0;v<8;v++) for(var u=0;u<8;u++){double sum=0;for(var y=0;y<8;y++)for(var x=0;x<8;x++)sum+=input[y*8+x]*Cos[x,u]*Cos[y,v];var cu=u==0?0.353553390593274:0.5;var cv=v==0?0.353553390593274:0.5;output[v*8+u]=(int)Math.Round(sum*cu*cv/q[v*8+u]);} return output; }
    private static double[,] BuildCos(){var c=new double[8,8];for(var x=0;x<8;x++)for(var u=0;u<8;u++)c[x,u]=Math.Cos((2*x+1)*u*Math.PI/16);return c;}
    private static void WriteValue(Bits bits,int value,int table,int run=0){var abs=Math.Abs(value);var size=abs==0?0:32-System.Numerics.BitOperations.LeadingZeroCount((uint)abs);if(table==0)bits.Write((uint)size,4);else { var symbol=(run<<4)|size; if(symbol==255) bits.Write(0x1FE,9); else bits.WriteByte((byte)symbol); }if(size>0)bits.Write((uint)(value<0?value-1:value),size);}
    private static void Marker(Stream s,byte marker){s.WriteByte(0xFF);s.WriteByte(marker);}
    private static void U16(Stream s,int value){s.WriteByte((byte)(value>>8));s.WriteByte((byte)value);}
    private static void WriteApp0(Stream s){Marker(s,0xE0);U16(s,16);s.Write("JFIF\0"u8);s.Write([1,1,0,0,1,0,1,0,0]);}
    private static void WriteQuant(Stream s,int id,int[] q){Marker(s,0xDB);U16(s,67);s.WriteByte((byte)id);for(var i=0;i<64;i++)s.WriteByte((byte)q[ZigZag[i]]);}
    private static void WriteFrame(Stream s,int w,int h){Marker(s,0xC0);U16(s,17);s.WriteByte(8);U16(s,h);U16(s,w);s.Write([3,1,0x11,0,2,0x11,1,3,0x11,1]);}
    private static void WriteHuffman(Stream s,int id,bool ac){Marker(s,0xC4);var count=ac?256:12;U16(s,3+16+count);s.WriteByte((byte)((ac?0x10:0)|id));for(var i=1;i<=16;i++)s.WriteByte((byte)(ac?(i==8?255:i==9?1:0):(i==4?count:0)));for(var i=0;i<count;i++)s.WriteByte((byte)i);}
    private sealed class Bits(Stream stream){private int _value,_count;public void Write(uint value,int count){for(var i=count-1;i>=0;i--)WriteBit((int)(value>>i)&1);}public void WriteByte(byte value)=>Write(value,8);private void WriteBit(int bit){_value=(_value<<1)|bit;if(++_count==8){_count=0;stream.WriteByte((byte)_value);if(_value==0xFF)stream.WriteByte(0);_value=0;}}public void Flush(){if(_count>0)Write((uint)((1<<(8-_count))-1),8-_count);}}
}
