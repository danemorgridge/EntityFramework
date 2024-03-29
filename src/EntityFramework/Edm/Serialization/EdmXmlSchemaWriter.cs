// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.Edm.Serialization
{
    using System.Collections.Generic;
    using System.Data.Entity.Core.Metadata.Edm;
    using System.Data.Entity.Core.Metadata.Edm.Provider;
    using System.Data.Entity.ModelConfiguration.Edm;
    using System.Data.Entity.Utilities;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Xml;

    internal class EdmXmlSchemaWriter : XmlSchemaWriter
    {
        private readonly bool _serializeDefaultNullability;
        private const string DataServicesPrefix = "m";
        private const string DataServicesNamespace = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
        private const string DataServicesMimeTypeAttribute = "System.Data.Services.MimeTypeAttribute";
        private const string DataServicesHasStreamAttribute = "System.Data.Services.Common.HasStreamAttribute";

        private const string DataServicesEntityPropertyMappingAttribute =
            "System.Data.Services.Common.EntityPropertyMappingAttribute";

        internal static class SyndicationXmlConstants
        {
            /// <summary>
            ///     author/email
            /// </summary>
            internal const string SyndAuthorEmail = "SyndicationAuthorEmail";

            /// <summary>
            ///     author/name
            /// </summary>
            internal const string SyndAuthorName = "SyndicationAuthorName";

            /// <summary>
            ///     author/uri
            /// </summary>
            internal const string SyndAuthorUri = "SyndicationAuthorUri";

            /// <summary>
            ///     published
            /// </summary>
            internal const string SyndPublished = "SyndicationPublished";

            /// <summary>
            ///     rights
            /// </summary>
            internal const string SyndRights = "SyndicationRights";

            /// <summary>
            ///     summary
            /// </summary>
            internal const string SyndSummary = "SyndicationSummary";

            /// <summary>
            ///     title
            /// </summary>
            internal const string SyndTitle = "SyndicationTitle";

            /// <summary>
            ///     contributor/email
            /// </summary>
            internal const string SyndContributorEmail = "SyndicationContributorEmail";

            /// <summary>
            ///     contributor/name
            /// </summary>
            internal const string SyndContributorName = "SyndicationContributorName";

            /// <summary>
            ///     contributor/uri
            /// </summary>
            internal const string SyndContributorUri = "SyndicationContributorUri";

            /// <summary>
            ///     category/@label
            /// </summary>
            internal const string SyndCategoryLabel = "SyndicationCategoryLabel";

            /// <summary>
            ///     Plaintext
            /// </summary>
            internal const string SyndContentKindPlaintext = "text";

            /// <summary>
            ///     HTML
            /// </summary>
            internal const string SyndContentKindHtml = "html";

            /// <summary>
            ///     XHTML
            /// </summary>
            internal const string SyndContentKindXHtml = "xhtml";

            /// <summary>
            ///     updated
            /// </summary>
            internal const string SyndUpdated = "SyndicationUpdated";

            /// <summary>
            ///     link/@href
            /// </summary>
            internal const string SyndLinkHref = "SyndicationLinkHref";

            /// <summary>
            ///     link/@rel
            /// </summary>
            internal const string SyndLinkRel = "SyndicationLinkRel";

            /// <summary>
            ///     link/@type
            /// </summary>
            internal const string SyndLinkType = "SyndicationLinkType";

            /// <summary>
            ///     link/@hreflang
            /// </summary>
            internal const string SyndLinkHrefLang = "SyndicationLinkHrefLang";

            /// <summary>
            ///     link/@title
            /// </summary>
            internal const string SyndLinkTitle = "SyndicationLinkTitle";

            /// <summary>
            ///     link/@length
            /// </summary>
            internal const string SyndLinkLength = "SyndicationLinkLength";

            /// <summary>
            ///     category/@term
            /// </summary>
            internal const string SyndCategoryTerm = "SyndicationCategoryTerm";

            /// <summary>
            ///     category/@scheme
            /// </summary>
            internal const string SyndCategoryScheme = "SyndicationCategoryScheme";
        }

        private static string SyndicationItemPropertyToString(object value)
        {
            return _syndicationItemToTargetPath[(int)value];
        }

        private static readonly string[] _syndicationItemToTargetPath
            = new[]
                  {
                      String.Empty,
                      // SyndicationItemProperty.Custom
                      SyndicationXmlConstants.SyndAuthorEmail,
                      SyndicationXmlConstants.SyndAuthorName,
                      SyndicationXmlConstants.SyndAuthorUri,
                      SyndicationXmlConstants.SyndContributorEmail,
                      SyndicationXmlConstants.SyndContributorName,
                      SyndicationXmlConstants.SyndContributorUri,
                      SyndicationXmlConstants.SyndUpdated,
                      SyndicationXmlConstants.SyndPublished,
                      SyndicationXmlConstants.SyndRights,
                      SyndicationXmlConstants.SyndSummary,
                      SyndicationXmlConstants.SyndTitle,
                      SyndicationXmlConstants.SyndCategoryLabel,
                      SyndicationXmlConstants.SyndCategoryScheme,
                      SyndicationXmlConstants.SyndCategoryTerm,
                      SyndicationXmlConstants.SyndLinkHref,
                      SyndicationXmlConstants.SyndLinkHrefLang,
                      SyndicationXmlConstants.SyndLinkLength,
                      SyndicationXmlConstants.SyndLinkRel,
                      SyndicationXmlConstants.SyndLinkTitle,
                      SyndicationXmlConstants.SyndLinkType
                  };

        private static string SyndicationTextContentKindToString(object value)
        {
            return _syndicationTextContentKindToString[(int)value];
        }

        private static readonly string[] _syndicationTextContentKindToString
            = new[]
                  {
                      SyndicationXmlConstants.
                          SyndContentKindPlaintext,
                      SyndicationXmlConstants.SyndContentKindHtml,
                      SyndicationXmlConstants.SyndContentKindXHtml
                  };

        public EdmXmlSchemaWriter()
        {
            // testing
        }

        internal EdmXmlSchemaWriter(XmlWriter xmlWriter, double edmVersion, bool serializeDefaultNullability)
        {
            DebugCheck.NotNull(xmlWriter);

            _serializeDefaultNullability = serializeDefaultNullability;
            _xmlWriter = xmlWriter;
            _version = edmVersion;
        }

        internal void WriteSchemaElementHeader(string schemaNamespace)
        {
            DebugCheck.NotEmpty(schemaNamespace);

            var xmlNamespace = XmlConstants.GetCsdlNamespace(_version);

            _xmlWriter.WriteStartElement(XmlConstants.Schema, xmlNamespace);
            _xmlWriter.WriteAttributeString(XmlConstants.Namespace, schemaNamespace);
            _xmlWriter.WriteAttributeString(XmlConstants.Alias, XmlConstants.Self);

            if (_version == XmlConstants.EdmVersionForV3)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.UseStrongSpatialTypes, XmlConstants.AnnotationNamespace,
                    XmlConstants.False);
            }
        }

        internal void WriteSchemaElementHeader(string schemaNamespace, string provider, string providerManifestToken)
        {
            DebugCheck.NotEmpty(schemaNamespace);
            DebugCheck.NotEmpty(provider);
            DebugCheck.NotEmpty(providerManifestToken);

            var xmlNamespace = XmlConstants.GetSsdlNamespace(_version);
            _xmlWriter.WriteStartElement(XmlConstants.Schema, xmlNamespace);
            _xmlWriter.WriteAttributeString(XmlConstants.Namespace, schemaNamespace);
            _xmlWriter.WriteAttributeString(XmlConstants.Provider, provider);
            _xmlWriter.WriteAttributeString(XmlConstants.ProviderManifestToken, providerManifestToken);
            _xmlWriter.WriteAttributeString(XmlConstants.Alias, XmlConstants.Self);
        }

        private void WritePolymorphicTypeAttributes(EdmType edmType)
        {
            DebugCheck.NotNull(edmType);

            if (edmType.BaseType != null)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.BaseType,
                    GetQualifiedTypeName(XmlConstants.Self, edmType.BaseType.Name));
            }

            if (edmType.Abstract)
            {
                _xmlWriter.WriteAttributeString(XmlConstants.Abstract, XmlConstants.True);
            }
        }

        public virtual void WriteFunctionElementHeader(EdmFunction function)
        {
            DebugCheck.NotNull(function);

            _xmlWriter.WriteStartElement(XmlConstants.Function);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, function.Name);
            _xmlWriter.WriteAttributeString(XmlConstants.AggregateAttribute, GetLowerCaseStringFromBoolValue(function.AggregateAttribute));
            _xmlWriter.WriteAttributeString(XmlConstants.BuiltInAttribute, GetLowerCaseStringFromBoolValue(function.BuiltInAttribute));
            _xmlWriter.WriteAttributeString(
                XmlConstants.NiladicFunction, GetLowerCaseStringFromBoolValue(function.NiladicFunctionAttribute));
            _xmlWriter.WriteAttributeString(XmlConstants.IsComposable, GetLowerCaseStringFromBoolValue(function.IsComposableAttribute));
            _xmlWriter.WriteAttributeString(XmlConstants.ParameterTypeSemantics, function.ParameterTypeSemanticsAttribute.ToString());
            _xmlWriter.WriteAttributeString(XmlConstants.Schema, function.Schema);
        }

        public virtual void WriteFunctionParameterHeader(FunctionParameter functionParameter)
        {
            DebugCheck.NotNull(functionParameter);

            _xmlWriter.WriteStartElement(XmlConstants.Parameter);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, functionParameter.Name);
            _xmlWriter.WriteAttributeString(XmlConstants.TypeAttribute, functionParameter.TypeName);
            _xmlWriter.WriteAttributeString(XmlConstants.Mode, functionParameter.Mode.ToString());
        }

        internal void WriteEntityTypeElementHeader(EntityType entityType)
        {
            DebugCheck.NotNull(entityType);

            _xmlWriter.WriteStartElement(XmlConstants.EntityType);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, entityType.Name);

            if (entityType.Annotations.GetClrAttributes() != null)
            {
                foreach (var a in entityType.Annotations.GetClrAttributes())
                {
                    if (a.GetType().FullName.Equals(DataServicesHasStreamAttribute, StringComparison.Ordinal))
                    {
                        _xmlWriter.WriteAttributeString(DataServicesPrefix, "HasStream", DataServicesNamespace, "true");
                    }
                    else if (a.GetType().FullName.Equals(DataServicesMimeTypeAttribute, StringComparison.Ordinal))
                    {
                        // Move down to the appropriate property
                        var propertyName = a.GetType().GetProperty("MemberName").GetValue(a, null) as string;
                        var property =
                            entityType.Properties.SingleOrDefault(
                                p => p.Name.Equals(propertyName, StringComparison.Ordinal));
                        AddAttributeAnnotation(property, a);
                    }
                    else if (a.GetType().FullName.Equals(
                        DataServicesEntityPropertyMappingAttribute, StringComparison.Ordinal))
                    {
                        // Move down to the appropriate property
                        var sourcePath = a.GetType().GetProperty("SourcePath").GetValue(a, null) as string;
                        var slashIndex = sourcePath.IndexOf("/", StringComparison.Ordinal);
                        string propertyName;
                        if (slashIndex == -1)
                        {
                            propertyName = sourcePath;
                        }
                        else
                        {
                            propertyName = sourcePath.Substring(0, slashIndex);
                        }
                        var property =
                            entityType.Properties.SingleOrDefault(
                                p => p.Name.Equals(propertyName, StringComparison.Ordinal));
                        AddAttributeAnnotation(property, a);
                    }
                }
            }

            WritePolymorphicTypeAttributes(entityType);
        }

        internal void WriteEnumTypeElementHeader(EnumType enumType)
        {
            DebugCheck.NotNull(enumType);

            _xmlWriter.WriteStartElement(XmlConstants.EnumType);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, enumType.Name);
            _xmlWriter.WriteAttributeString(
                XmlConstants.IsFlags, GetLowerCaseStringFromBoolValue(enumType.IsFlags));

            if (enumType.UnderlyingType != null)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.UnderlyingType,
                    enumType.UnderlyingType.PrimitiveTypeKind.ToString());
            }
        }

        internal void WriteEnumTypeMemberElementHeader(EnumMember enumTypeMember)
        {
            DebugCheck.NotNull(enumTypeMember);

            _xmlWriter.WriteStartElement(XmlConstants.Member);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, enumTypeMember.Name);
            _xmlWriter.WriteAttributeString(XmlConstants.Value, enumTypeMember.Value.ToString());
        }

        private static void AddAttributeAnnotation(EdmProperty property, Attribute a)
        {
            if (property != null)
            {
                var clrAttributes = property.Annotations.GetClrAttributes();
                if (clrAttributes != null)
                {
                    if (!clrAttributes.Contains(a))
                    {
                        clrAttributes.Add(a);
                    }
                }
                else
                {
                    property.Annotations.SetClrAttributes(
                        new List<Attribute>
                            {
                                a
                            });
                }
            }
        }

        internal void WriteComplexTypeElementHeader(ComplexType complexType)
        {
            DebugCheck.NotNull(complexType);

            _xmlWriter.WriteStartElement(XmlConstants.ComplexType);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, complexType.Name);
            WritePolymorphicTypeAttributes(complexType);
        }

        internal void WriteAssociationTypeElementHeader(AssociationType associationType)
        {
            DebugCheck.NotNull(associationType);

            _xmlWriter.WriteStartElement(XmlConstants.Association);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, associationType.Name);
        }

        internal void WriteAssociationEndElementHeader(RelationshipEndMember associationEnd)
        {
            DebugCheck.NotNull(associationEnd);

            _xmlWriter.WriteStartElement(XmlConstants.End);
            _xmlWriter.WriteAttributeString(XmlConstants.Role, associationEnd.Name);

            var typeName = associationEnd.GetEntityType().Name;
            _xmlWriter.WriteAttributeString(
                XmlConstants.TypeAttribute, GetQualifiedTypeName(XmlConstants.Self, typeName));
            _xmlWriter.WriteAttributeString(
                XmlConstants.Multiplicity, GetXmlMultiplicity(associationEnd.RelationshipMultiplicity));
        }

        internal void WriteOperationActionElement(string elementName, OperationAction operationAction)
        {
            DebugCheck.NotEmpty(elementName);

            _xmlWriter.WriteStartElement(elementName);
            _xmlWriter.WriteAttributeString(XmlConstants.Action, operationAction.ToString());
            _xmlWriter.WriteEndElement();
        }

        internal void WriteReferentialConstraintElementHeader()
        {
            _xmlWriter.WriteStartElement(XmlConstants.ReferentialConstraint);
        }

        internal void WriteDelaredKeyPropertiesElementHeader()
        {
            _xmlWriter.WriteStartElement(XmlConstants.Key);
        }

        internal void WriteDelaredKeyPropertyRefElement(EdmProperty property)
        {
            DebugCheck.NotNull(property);

            _xmlWriter.WriteStartElement(XmlConstants.PropertyRef);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, property.Name);
            _xmlWriter.WriteEndElement();
        }

        [Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        internal void WritePropertyElementHeader(EdmProperty property)
        {
            DebugCheck.NotNull(property);

            _xmlWriter.WriteStartElement(XmlConstants.Property);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, property.Name);
            _xmlWriter.WriteAttributeString(XmlConstants.TypeAttribute, GetTypeReferenceName(property));

            if (property.CollectionKind != CollectionKind.None)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.CollectionKind, property.CollectionKind.ToString());
            }

            if (property.ConcurrencyMode == ConcurrencyMode.Fixed)
            {
                _xmlWriter.WriteAttributeString(EdmProviderManifest.ConcurrencyModeFacetName, XmlConstants.Fixed);
            }

            if (property.Annotations.GetClrAttributes() != null)
            {
                var epmCount = 0;
                foreach (var a in property.Annotations.GetClrAttributes())
                {
                    if (a.GetType().FullName.Equals(DataServicesMimeTypeAttribute, StringComparison.Ordinal))
                    {
                        var mimeType = a.GetType().GetProperty("MimeType").GetValue(a, null) as string;
                        _xmlWriter.WriteAttributeString(DataServicesPrefix, "MimeType", DataServicesNamespace, mimeType);
                    }
                    else if (a.GetType().FullName.Equals(
                        DataServicesEntityPropertyMappingAttribute, StringComparison.Ordinal))
                    {
                        var suffix = epmCount == 0
                                         ? String.Empty
                                         : string.Format(CultureInfo.InvariantCulture, "_{0}", epmCount);

                        var sourcePath = a.GetType().GetProperty("SourcePath").GetValue(a, null) as string;
                        var slashIndex = sourcePath.IndexOf("/", StringComparison.Ordinal);
                        if (slashIndex != -1
                            && slashIndex + 1 < sourcePath.Length)
                        {
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix, "FC_SourcePath" + suffix, DataServicesNamespace,
                                sourcePath.Substring(slashIndex + 1));
                        }

                        // There are three ways to write out this attribute
                        var syndicationItem = a.GetType().GetProperty("TargetSyndicationItem").GetValue(a, null);
                        var keepInContext = a.GetType().GetProperty("KeepInContent").GetValue(a, null).ToString();
                        var criteriaValueProperty = a.GetType().GetProperty("CriteriaValue");
                        string criteriaValue = null;
                        if (criteriaValueProperty != null)
                        {
                            criteriaValue = criteriaValueProperty.GetValue(a, null) as string;
                        }

                        if (criteriaValue != null)
                        {
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix,
                                "FC_TargetPath" + suffix,
                                DataServicesNamespace,
                                SyndicationItemPropertyToString(syndicationItem));
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix, "FC_KeepInContent" + suffix, DataServicesNamespace,
                                keepInContext);
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix, "FC_CriteriaValue" + suffix, DataServicesNamespace,
                                criteriaValue);
                        }
                        else if (string.Equals(
                            syndicationItem.ToString(), "CustomProperty", StringComparison.Ordinal))
                        {
                            var targetPath = a.GetType().GetProperty("TargetPath").GetValue(a, null).ToString();
                            var targetNamespacePrefix =
                                a.GetType().GetProperty("TargetNamespacePrefix").GetValue(a, null).ToString();
                            var targetNamespaceUri =
                                a.GetType().GetProperty("TargetNamespaceUri").GetValue(a, null).ToString();

                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix, "FC_TargetPath" + suffix, DataServicesNamespace, targetPath);
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix, "FC_NsUri" + suffix, DataServicesNamespace,
                                targetNamespaceUri);
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix, "FC_NsPrefix" + suffix, DataServicesNamespace,
                                targetNamespacePrefix);
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix, "FC_KeepInContent" + suffix, DataServicesNamespace,
                                keepInContext);
                        }
                        else
                        {
                            var contextKind = a.GetType().GetProperty("TargetTextContentKind").GetValue(a, null);

                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix,
                                "FC_TargetPath" + suffix,
                                DataServicesNamespace,
                                SyndicationItemPropertyToString(syndicationItem));
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix,
                                "FC_ContentKind" + suffix,
                                DataServicesNamespace,
                                SyndicationTextContentKindToString(contextKind));
                            _xmlWriter.WriteAttributeString(
                                DataServicesPrefix, "FC_KeepInContent" + suffix, DataServicesNamespace,
                                keepInContext);
                        }

                        epmCount++;
                    }
                }
            }

            if (property.IsMaxLength)
            {
                _xmlWriter.WriteAttributeString(XmlConstants.MaxLengthElement, XmlConstants.Max);
            }
            else if (!property.IsMaxLengthConstant && property.MaxLength.HasValue)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.MaxLengthElement,
                    property.MaxLength.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (!property.IsFixedLengthConstant && property.IsFixedLength.HasValue)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.FixedLengthElement,
                    GetLowerCaseStringFromBoolValue(property.IsFixedLength.Value));
            }

            if (!property.IsUnicodeConstant && property.IsUnicode.HasValue)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.UnicodeElement, GetLowerCaseStringFromBoolValue(property.IsUnicode.Value));
            }

            if (!property.IsPrecisionConstant && property.Precision.HasValue)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.PrecisionElement,
                    property.Precision.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (!property.IsScaleConstant && property.Scale.HasValue)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.ScaleElement, property.Scale.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (property.StoreGeneratedPattern != StoreGeneratedPattern.None)
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.StoreGeneratedPattern,
                    property.StoreGeneratedPattern == StoreGeneratedPattern.Computed
                        ? XmlConstants.Computed
                        : XmlConstants.Identity);
            }

            if (_serializeDefaultNullability || !property.Nullable)
            {
                _xmlWriter.WriteAttributeString(
                    EdmConstants.Nullable, GetLowerCaseStringFromBoolValue(property.Nullable));
            }

            MetadataProperty metadataProperty;
            if (property.MetadataProperties.TryGetValue(XmlConstants.StoreGeneratedPatternAnnotation, false, out metadataProperty))
            {
                _xmlWriter.WriteAttributeString(
                    XmlConstants.StoreGeneratedPattern, XmlConstants.AnnotationNamespace,
                    metadataProperty.Value.ToString());
            }
        }

        private static string GetTypeReferenceName(EdmProperty property)
        {
            DebugCheck.NotNull(property);

            if (property.IsPrimitiveType)
            {
                return property.TypeName;
            }

            if (property.IsComplexType)
            {
                return GetQualifiedTypeName(XmlConstants.Self, property.ComplexType.Name);
            }

            Debug.Assert(property.IsEnumType);

            return GetQualifiedTypeName(XmlConstants.Self, property.EnumType.Name);
        }

        internal void WriteNavigationPropertyElementHeader(NavigationProperty member)
        {
            _xmlWriter.WriteStartElement(XmlConstants.NavigationProperty);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, member.Name);
            _xmlWriter.WriteAttributeString(
                XmlConstants.Relationship,
                GetQualifiedTypeName(XmlConstants.Self, member.Association.Name));
            _xmlWriter.WriteAttributeString(XmlConstants.FromRole, member.GetFromEnd().Name);
            _xmlWriter.WriteAttributeString(XmlConstants.ToRole, member.ToEndMember.Name);
        }

        private static string GetXmlMultiplicity(RelationshipMultiplicity endKind)
        {
            switch (endKind)
            {
                case RelationshipMultiplicity.Many:
                    return "*";
                case RelationshipMultiplicity.One:
                    return "1";
                case RelationshipMultiplicity.ZeroOrOne:
                    return "0..1";
                default:
                    Debug.Fail("Did you add a new EdmAssociationEndKind?");
                    return string.Empty;
            }
        }

        internal void WriteReferentialConstraintRoleElement(
            string roleName, RelationshipEndMember edmAssociationEnd, IEnumerable<EdmProperty> properties)
        {
            _xmlWriter.WriteStartElement(roleName);
            _xmlWriter.WriteAttributeString(XmlConstants.Role, edmAssociationEnd.Name);

            foreach (var property in properties)
            {
                _xmlWriter.WriteStartElement(XmlConstants.PropertyRef);
                _xmlWriter.WriteAttributeString(XmlConstants.Name, property.Name);
                _xmlWriter.WriteEndElement();
            }

            _xmlWriter.WriteEndElement();
        }

        internal void WriteEntityContainerElementHeader(EntityContainer container)
        {
            DebugCheck.NotNull(container);

            _xmlWriter.WriteStartElement(XmlConstants.EntityContainer);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, container.Name);
        }

        internal void WriteAssociationSetElementHeader(AssociationSet associationSet)
        {
            DebugCheck.NotNull(associationSet);

            _xmlWriter.WriteStartElement(XmlConstants.AssociationSet);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, associationSet.Name);
            _xmlWriter.WriteAttributeString(
                XmlConstants.Association,
                GetQualifiedTypeName(XmlConstants.Self, associationSet.ElementType.Name));
        }

        internal void WriteAssociationSetEndElement(EntitySet end, string roleName)
        {
            DebugCheck.NotNull(end);
            DebugCheck.NotEmpty(roleName);

            _xmlWriter.WriteStartElement(XmlConstants.End);
            _xmlWriter.WriteAttributeString(XmlConstants.Role, roleName);
            _xmlWriter.WriteAttributeString(XmlConstants.EntitySet, end.Name);
            _xmlWriter.WriteEndElement();
        }

        internal void WriteEntitySetElementHeader(EntitySet entitySet)
        {
            DebugCheck.NotNull(entitySet);

            _xmlWriter.WriteStartElement(XmlConstants.EntitySet);
            _xmlWriter.WriteAttributeString(XmlConstants.Name, entitySet.Name);
            _xmlWriter.WriteAttributeString(
                XmlConstants.EntityType,
                GetQualifiedTypeName(XmlConstants.Self, entitySet.ElementType.Name));

            if (!string.IsNullOrWhiteSpace(entitySet.Schema))
            {
                _xmlWriter.WriteAttributeString(XmlConstants.Schema, entitySet.Schema);
            }

            if (!string.IsNullOrWhiteSpace(entitySet.Table))
            {
                _xmlWriter.WriteAttributeString(XmlConstants.Table, entitySet.Table);
            }

            WriteExtendedProperties(entitySet);
        }

        internal void WriteDefiningQuery(EntitySet entitySet)
        {
            DebugCheck.NotNull(entitySet);

            if (!string.IsNullOrWhiteSpace(entitySet.DefiningQuery))
            {
                _xmlWriter.WriteElementString(XmlConstants.DefiningQuery, entitySet.DefiningQuery);
            }
        }

        private void WriteExtendedProperties(MetadataItem item)
        {
            DebugCheck.NotNull(item);

            foreach (var extendedProperty in item.MetadataProperties.Where(p => p.PropertyKind == PropertyKind.Extended))
            {
                string xmlNamespaceUri, attributeName;
                if (TrySplitExtendedMetadataPropertyName(extendedProperty.Name, out xmlNamespaceUri, out attributeName))
                {
                    DebugCheck.NotNull(extendedProperty.Value);

                    _xmlWriter.WriteAttributeString(attributeName, xmlNamespaceUri, extendedProperty.Value.ToString());
                }
            }
        }

        private static bool TrySplitExtendedMetadataPropertyName(string name, out string xmlNamespaceUri, out string attributeName)
        {
            var pos = name.LastIndexOf(':');
            if (pos < 1
                || name.Length <= pos + 1)
            {
                xmlNamespaceUri = null;
                attributeName = null;
                return false;
            }

            xmlNamespaceUri = name.Substring(0, pos);
            attributeName = name.Substring(pos + 1, (name.Length - 1) - pos);
            return true;
        }
    }
}
