namespace Haiyu.ABI.Services.GameDataExport.Protocol;

/// <summary>兼容入口；解析与按需 JSON 编码分别保存状态。</summary>
internal sealed class ProtocolJsonDecoder(ProtocolCatalog catalog)
{
    internal sealed class Result
    {
        private readonly ProtocolJsonWriter _writer;
        private readonly ProtocolDecodeResult _decoded;
        private bool _encoded;
        private byte[]? _json;
        internal Result(ProtocolDecodeResult decoded, ProtocolJsonWriter writer) { _decoded = decoded; _writer = writer; }
        internal string Status => EncodingError is null ? _decoded.Status : "json_limit";
        internal string DecodeStatus => _decoded.Status;
        internal string? MessageType => _decoded.MessageType;
        internal ProtocolMessage? Message => _decoded.Message;
        internal bool HasUnknownFields => _decoded.HasUnknownFields;
        internal string? Error => EncodingError ?? _decoded.Error;
        internal string? EncodingError { get; private set; }
        internal byte[]? Json
        {
            get
            {
                if (_encoded) return _json;
                _encoded = true;
                if (Message is null) return null;
                try { _json = _writer.Encode(Message); }
                catch (OutputLimitException ex) { EncodingError = ex.Message; }
                return _json;
            }
        }
    }
    private readonly ProtocolDecoder _decoder = new(catalog);
    private readonly ProtocolJsonWriter _writer = new(catalog);
    internal Result Decode(int msgId, byte[] bytes) => new(_decoder.Decode(msgId, bytes), _writer);
    internal Result DecodeType(string type, byte[] bytes) => new(_decoder.DecodeType(type, bytes), _writer);
}
