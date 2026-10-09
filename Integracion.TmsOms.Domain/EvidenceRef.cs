namespace Integracion.TmsOms.Domain
{
    public sealed record EvidenceRef(
        string Label, 
        string FileType, 
        string FileName, 
        string Url
    );
}
