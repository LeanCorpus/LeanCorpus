using Rowles.LeanCorpus.Codecs.PackedBkd;

namespace Rowles.LeanCorpus.Index.Segment;

internal enum SpatialPointFieldResolution : byte
{
    None,
    GeoPoint,
    XYPoint,
    LegacyGeo,
    OtherSpatial
}

/// <summary>Centralises the semantic and physical admission rules for Geo and XY point fields.</summary>
internal static class SpatialPointFieldCompatibility
{
    internal static SpatialPointFieldResolution Resolve(SegmentInfo info, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(info);
        return ResolveExplicit(info.SpatialFields, fieldName);
    }

    internal static SpatialPointFieldResolution Resolve(SegmentDescriptor info, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(info);
        return ResolveExplicit(info.SpatialFields, fieldName);
    }

    internal static SpatialPointFieldResolution Resolve(SegmentReader reader, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(fieldName);

        SpatialPointFieldResolution explicitResolution = ResolveExplicit(reader.Info.SpatialFields, fieldName);
        if (explicitResolution != SpatialPointFieldResolution.None)
            return explicitResolution;

        return reader.HasNumericField(fieldName + "_lat") && reader.HasNumericField(fieldName + "_lon")
            ? SpatialPointFieldResolution.LegacyGeo
            : SpatialPointFieldResolution.None;
    }

    internal static bool TryGetCompatiblePackedField(
        SegmentReader reader,
        string fieldName,
        SpatialFieldKind expectedKind,
        out PackedBkdFieldMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(fieldName);
        if (expectedKind is not (SpatialFieldKind.GeoPoint or SpatialFieldKind.XYPoint))
            throw new ArgumentOutOfRangeException(nameof(expectedKind), expectedKind, "A packed point query requires a point field kind.");

        SpatialPointFieldResolution expectedResolution = expectedKind == SpatialFieldKind.GeoPoint
            ? SpatialPointFieldResolution.GeoPoint
            : SpatialPointFieldResolution.XYPoint;
        if (Resolve(reader, fieldName) != expectedResolution
            || !reader.TryGetPackedBkdFieldMetadata(fieldName, out metadata)
            || !HasCompatiblePointLayout(metadata))
        {
            metadata = default;
            return false;
        }

        return true;
    }

    internal static bool HasCompatiblePointLayout(PackedBkdFieldMetadata metadata)
        => metadata.Config.Dimensions == 2
            && metadata.Config.IndexedDimensions == 2
            && metadata.Config.BytesPerDimension == PackedBkdConfig.FixedBytesPerDimension;

    private static SpatialPointFieldResolution ResolveExplicit(
        IReadOnlyList<SpatialFieldInfo> spatialFields,
        string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        foreach (SpatialFieldInfo spatialField in spatialFields)
        {
            if (!string.Equals(spatialField.FieldName, fieldName, StringComparison.Ordinal))
                continue;

            return spatialField.Kind switch
            {
                SpatialFieldKind.GeoPoint => SpatialPointFieldResolution.GeoPoint,
                SpatialFieldKind.XYPoint => SpatialPointFieldResolution.XYPoint,
                _ => SpatialPointFieldResolution.OtherSpatial
            };
        }

        return SpatialPointFieldResolution.None;
    }
}
