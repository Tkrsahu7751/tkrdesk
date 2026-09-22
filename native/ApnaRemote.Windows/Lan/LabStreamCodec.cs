namespace ApnaRemote.Windows.Lan;

/// <summary>Host outbound video codec for the TLS LAN session.</summary>
internal enum LabStreamCodec
{
    /// <summary>Reliable default — one JPEG per frame.</summary>
    Jpeg = 0,
    /// <summary>H.264 Annex-B via OpenH264 (Windows↔Windows lab).</summary>
    H264 = 1,
}
