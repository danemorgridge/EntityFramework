﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.Core.Metadata.Edm
{
    using Xunit;

    public class AssociationTypeTests
    {
        [Fact]
        public void Can_get_and_set_ends_via_wrapper_properties()
        {
            var associationType = new AssociationType("A", XmlConstants.ModelNamespace_3, false, DataSpace.CSpace);

            Assert.Null(associationType.SourceEnd);
            Assert.Null(associationType.TargetEnd);

            var sourceEnd = new AssociationEndMember("S", new EntityType("E", "N", DataSpace.CSpace));

            associationType.SourceEnd = sourceEnd;

            var targetEnd = new AssociationEndMember("T", new EntityType("E", "N", DataSpace.CSpace));

            associationType.TargetEnd = targetEnd;

            Assert.Same(sourceEnd, associationType.SourceEnd);
            Assert.Same(targetEnd, associationType.TargetEnd);
        }

        [Fact]
        public void Can_get_and_set_constraint_via_wrapper_property()
        {
            var associationType
                = new AssociationType("A", XmlConstants.ModelNamespace_3, false, DataSpace.CSpace)
                      {
                          SourceEnd = new AssociationEndMember("S", new EntityType("E", "N", DataSpace.CSpace)),
                          TargetEnd = new AssociationEndMember("T", new EntityType("E", "N", DataSpace.CSpace))
                      };

            Assert.Null(associationType.Constraint);
            Assert.False(associationType.IsForeignKey);

            var property
                = EdmProperty.Primitive("Fk", PrimitiveType.GetEdmPrimitiveType(PrimitiveTypeKind.String));

            var referentialConstraint
                = new ReferentialConstraint(
                    associationType.SourceEnd,
                    associationType.TargetEnd,
                    new[] { property },
                    new[] { property });

            associationType.Constraint = referentialConstraint;

            Assert.Same(referentialConstraint, associationType.Constraint);
            Assert.True(associationType.IsForeignKey);
        }
    }
}
