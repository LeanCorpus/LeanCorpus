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
        return Resolve(info.SpatialFields, info.FieldNames, fieldName);
    }

    internal static SpatialPointFieldResolution Resolve(SegmentDescriptor info, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(info);
        return Resolve(info.SpatialFields, info.FieldNames, fieldName);
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
        if (Resolve(reader.Info, fieldName) != expectedResolution
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

    private static SpatialPointFieldResolution Resolve(
        IReadOnlyList<SpatialFieldInfo> spatialFields,
        IReadOnlyList<string> fieldNames,
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

        bool hasLatitude = ContainsOrdinal(fieldNames, fieldName + "_lat");
        bool hasLongitude = ContainsOrdinal(fieldNames, fieldName + "_lon");
        return hasLatitude && hasLongitude
            ? SpatialPointFieldResolution.LegacyGeo
            : SpatialPointFieldResolution.None;
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> values, string value)
    {
        foreach (string candidate in values)
            if (string.Equals(candidate, value, StringComparison.Ordinal))
                return true;
        return false;
    }
}
