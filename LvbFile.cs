using Lumina.Data;

namespace ReshadeController;

// Minimal port of Weatherman's LvbFile (from titleedit via lmcintyre):
// reads the zone .lvb to get the supported weather id list + order.
// The envb filename parse is dropped (not needed for the override).
public unsafe class LvbFile : FileResource
{
    public ushort[] weatherIds = System.Array.Empty<ushort>();

    public override void LoadFile()
    {
        weatherIds = new ushort[32];

        var pos = 0xC;
        if (Data[pos] != 'S' || Data[pos + 1] != 'C' || Data[pos + 2] != 'N' || Data[pos + 3] != '1')
            pos += 0x14;
        var sceneChunkStart = pos;
        pos += 0x10;
        var settingsStart = sceneChunkStart + 8 + System.BitConverter.ToInt32(Data, pos);
        pos = settingsStart + 0x40;
        var weatherTableStart = settingsStart + System.BitConverter.ToInt32(Data, pos);
        pos = weatherTableStart;
        for (var i = 0; i < 32; i++)
            weatherIds[i] = System.BitConverter.ToUInt16(Data, pos + i * 2);
    }
}
