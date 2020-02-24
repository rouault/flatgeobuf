using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

using FlatBuffers;
using NetTopologySuite.Features;
using FlatGeobuf.Index;
using GeoAPI.Geometries;

namespace FlatGeobuf.NTS
{
    public class ColumnMeta
    {
        public string Name { get; set; }
        public ColumnType Type { get; set; }
    }

    public class LayerMeta
    {
        public string Name { get; set; }
        public GeometryType GeometryType { get; set; }
        public byte Dimensions { get; set; }
        public IList<ColumnMeta> Columns { get; set; }
    }

    public static class FeatureCollectionConversions
    {
        private class FeatureItem : Item
        {
            public int Size { get; set; }
        }

        public static byte[] Serialize(
            FeatureCollection fc,
            GeometryType geometryType,
            byte dimensions = 2,
            long featuresCount = 0,
            bool spatialIndex = false,
            IList<ColumnMeta> columns = null)
        {
            var featureFirst = fc.Features.First();
            if (columns == null && featureFirst.Attributes != null)
                    columns = featureFirst.Attributes.GetNames()
                        .Select(n => new ColumnMeta() { Name = n, Type = ToColumnType(featureFirst.Attributes.GetType(n)) })
                        .ToList();
            using (var memoryStream = new MemoryStream())
            {
                Serialize(memoryStream, fc.Features, geometryType, dimensions, featuresCount, spatialIndex, columns);
                return memoryStream.ToArray();
            }
        }

        public static void Serialize(
            Stream output,
            IEnumerable<IFeature> features,            
            GeometryType geometryType,
            byte dimensions = 2,
            long featuresCount = 0,
            bool spatialIndex = false,
            IList<ColumnMeta> columns = null)
        {
            if (spatialIndex)
            {
                var tempFileName = Path.GetTempFileName();
                var tempStreamWrite = new FileStream(tempFileName, FileMode.Open, FileAccess.Write);
                var indexData = new MemoryStream();
                var writer = new BinaryWriter(indexData);
                var items = new List<Item>();
                ulong offset = 0;
                ulong numItems = 0;
                double minX = double.MaxValue;
                double minY = double.MaxValue;
                double maxX = double.MinValue;
                double maxY = double.MinValue;
                foreach (var feature in features)
                {
                    var buffer = FeatureConversions.ToByteBuffer(feature, GeometryType.Unknown, dimensions, columns);
                    tempStreamWrite.Write(buffer);
                    var e = feature.Geometry.EnvelopeInternal;
                    items.Add(new FeatureItem() {
                        MinX = e.MinX,
                        MinY = e.MinY,
                        MaxX = e.MaxX,
                        MaxY = e.MaxY,
                        Offset = offset,
                        Size = buffer.Count()
                    });
                    if (e.MinX < minX) minX = e.MinX;
                    if (e.MinY < minY) minY = e.MinY;
                    if (e.MaxX > maxX) maxX = e.MaxX;
                    if (e.MaxY > maxY) maxY = e.MaxY;
                    offset += (ulong) buffer.LongCount();
                    numItems++;
                }
                var width = maxX - minX;
                var height = maxY - minY;
                var hilbertMax = (1 << 16) - 1;
                var sortedItems = items.OrderBy(i => {
                    var x = (uint) Math.Floor(hilbertMax * ((i.MinX + i.MaxX) / 2 - minX) / width);
                    var y = (uint) Math.Floor(hilbertMax * ((i.MinY + i.MaxY) / 2 - minY) / height);
                    return PackedRTree.Hilbert(x, y);
                }).ToList();
                // TODO: recalc offsets in sortedItems
                PackedRTree tree = new PackedRTree(sortedItems, 16);
                output.Write(Constants.MagicBytes);
                var header = BuildHeader(0, geometryType, columns, null);
                output.Write(header);
                // TODO: write tree index
                // TODO: rewrite features in sorted order
                return;
            }
            else
            {
                output.Write(Constants.MagicBytes);
                var header = BuildHeader(0, geometryType, columns, null);
                output.Write(header);
                foreach (var feature in features)
                {
                    var featureGeometryType = geometryType == GeometryType.Unknown ? GeometryConversions.ToGeometryType(feature.Geometry) : geometryType;
                    var buffer = FeatureConversions.ToByteBuffer(feature, featureGeometryType, dimensions, columns);
                    output.Write(buffer);
                }
            }
        }

        private static ColumnType ToColumnType(Type type)
        {
            switch (Type.GetTypeCode(type)) {
                case TypeCode.Byte: return ColumnType.UByte;
                case TypeCode.SByte: return ColumnType.Byte;
                case TypeCode.Boolean: return ColumnType.Bool;
                case TypeCode.Int32: return ColumnType.Int;
                case TypeCode.Int64: return ColumnType.Long;
                case TypeCode.Double: return ColumnType.Double;
                case TypeCode.String: return ColumnType.String;
                default: throw new ApplicationException("Unknown type");
            }
        }

        public static FeatureCollection Deserialize(byte[] bytes)
        {
            var fc = new NetTopologySuite.Features.FeatureCollection();

            foreach (var feature in Deserialize(new MemoryStream(bytes)))
                fc.Add(feature);

            return fc;
        }

        public static IEnumerable<IFeature> Deserialize(Stream stream, Envelope rect = null)
        {
            var reader = new BinaryReader(stream);

            var magicBytes = reader.ReadBytes(8);
            if (!magicBytes.SequenceEqual(Constants.MagicBytes))
                throw new Exception("Not a FlatGeobuf file");

            var headerSize = reader.ReadInt32();
            var header = Header.GetRootAsHeader(new ByteBuffer(reader.ReadBytes(headerSize)));
            
            var count = header.FeaturesCount;
            var nodeSize = header.IndexNodeSize;
            var geometryType = header.GeometryType;

            IList<ColumnMeta> columns = null;
            if (header.ColumnsLength > 0)
            {
                columns = new List<ColumnMeta>();
                for (int i = 0; i < header.ColumnsLength; i++){
                    var column = header.Columns(i).Value;
                    columns.Add(new ColumnMeta() { Name = column.Name, Type = column.Type });
                }
            }

            if (nodeSize > 0)
            {
                long offset = 8 + 4 + headerSize;
                var size = PackedRTree.CalcSize(count, nodeSize);
                if (rect != null) {
                    var result = PackedRTree.StreamSearch(count, nodeSize, rect, (ulong treeOffset, ulong size) => {
                        stream.Seek(offset + (long) treeOffset, SeekOrigin.Begin);
                        return stream;
                    }).ToList();
                    foreach (var item in result) {
                        stream.Seek(offset + (long) size + (long) item.Offset, SeekOrigin.Begin);
                        var featureLength = reader.ReadInt32();
                        var feature = FeatureConversions.FromByteBuffer(new ByteBuffer(reader.ReadBytes(featureLength)), geometryType, 2, columns);
                        yield return feature;
                    }
                    yield break;
                }
                stream.Seek(8 + 4 + headerSize + (long) size, SeekOrigin.Begin);
            }

            while (stream.Position < stream.Length)
            {
                var featureLength = reader.ReadInt32();
                var feature = FeatureConversions.FromByteBuffer(new ByteBuffer(reader.ReadBytes(featureLength)), geometryType, 2, columns);
                yield return feature;
            }
        }

        private static byte[] BuildHeader(ulong count, GeometryType geometryType, IList<ColumnMeta> columns, PackedRTree index)
        {
            var builder = new FlatBufferBuilder(4096);

            VectorOffset? columnsOffset = null;
            if (columns != null)
            {
                var columnsArray = columns
                    .Select(c => Column.CreateColumn(builder, builder.CreateString(c.Name), c.Type))
                    .ToArray();
                columnsOffset = Header.CreateColumnsVector(builder, columnsArray);
            }

            Header.StartHeader(builder);
            Header.AddGeometryType(builder, geometryType);
            if (columnsOffset.HasValue)
                Header.AddColumns(builder, columnsOffset.Value);
            //if (index != null)
            Header.AddIndexNodeSize(builder, 0);
            Header.AddFeaturesCount(builder, count);
            var offset = Header.EndHeader(builder);

            builder.FinishSizePrefixed(offset.Value);

            return builder.DataBuffer.ToSizedArray();
        }
    }
}