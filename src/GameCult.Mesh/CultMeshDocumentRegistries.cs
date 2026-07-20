using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Caching;
using GameCult.Networking;

namespace GameCult.Mesh
{
    public static partial class CultMesh
    {
        /// <summary>
        /// Creates a CultCache document registry and resolves the supplied document types into it.
        /// </summary>
        public static CultDocumentRegistry CreateCultCacheDocumentRegistry(params Type[] documentTypes)
        {
            return CreateCultCacheDocumentRegistry((IEnumerable<Type>)documentTypes);
        }

        /// <summary>
        /// Creates a CultCache document registry and resolves the supplied document types into it.
        /// </summary>
        public static CultDocumentRegistry CreateCultCacheDocumentRegistry(IEnumerable<Type> documentTypes)
        {
            if (documentTypes == null)
            {
                throw new ArgumentNullException(nameof(documentTypes));
            }

            return CultDocumentRegistry.ForTypes(DistinctDocumentTypes(documentTypes));
        }

        /// <summary>
        /// Creates a CultNet document registry with default bindings for the supplied document types.
        /// </summary>
        public static CultNetDocumentRegistry CreateCultNetDocumentRegistry(params Type[] documentTypes)
        {
            return CreateCultNetDocumentRegistry((IEnumerable<Type>)documentTypes);
        }

        /// <summary>
        /// Creates a CultNet document registry with default bindings for the supplied document types.
        /// </summary>
        public static CultNetDocumentRegistry CreateCultNetDocumentRegistry(IEnumerable<Type> documentTypes)
        {
            return CreateCultNetDocumentRegistry(documentTypes, null);
        }

        /// <summary>
        /// Creates a CultNet document registry with default bindings for the supplied document types.
        /// </summary>
        public static CultNetDocumentRegistry CreateCultNetDocumentRegistry(
            IEnumerable<Type> documentTypes,
            CultDocumentRegistry? documents)
        {
            if (documentTypes == null)
            {
                throw new ArgumentNullException(nameof(documentTypes));
            }

            documents ??= CreateCultCacheDocumentRegistry(documentTypes);
            var registry = new CultNetDocumentRegistry(documents);
            var descriptors = DistinctDocumentTypes(documentTypes)
                .Select(documentType => documents.GetRequired(documentType))
                .ToArray();
            foreach (var descriptorGroup in descriptors.GroupBy(descriptor => descriptor.SchemaId, StringComparer.Ordinal))
            {
                foreach (var descriptor in descriptorGroup.Reverse())
                {
                    registry.Register(CultNetDocumentBinding.ForDocument(descriptor.DocumentType, documents));
                }
            }

            return registry;
        }

        /// <summary>
        /// Creates a CultNet document registry with default bindings for the supplied document types.
        /// </summary>
        public static CultNetDocumentRegistry CreateCultNetDocumentRegistry(
            CultDocumentRegistry documents,
            params Type[] documentTypes)
        {
            return CreateCultNetDocumentRegistry(documentTypes, documents);
        }

        /// <summary>
        /// Creates a CultNet document registry with default bindings for the supplied document types.
        /// </summary>
        public static CultNetDocumentRegistry CreateCultNetDocumentRegistry(
            CultDocumentRegistry documents,
            IEnumerable<Type> documentTypes)
        {
            return CreateCultNetDocumentRegistry(documentTypes, documents);
        }

        private static IEnumerable<Type> DistinctDocumentTypes(IEnumerable<Type> documentTypes)
        {
            return documentTypes
                .Select(documentType => documentType ?? throw new ArgumentException(
                    "Document type collections cannot contain null entries.",
                    nameof(documentTypes)))
                .Distinct();
        }
    }
}
