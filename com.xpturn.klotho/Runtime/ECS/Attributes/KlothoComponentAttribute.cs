using System;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// ECS component type discriminator (16-bit storage). Independent of
    /// <see cref="xpTURN.Klotho.Serialization.KlothoSerializableAttribute.TypeId"/> and
    /// <see cref="KlothoDataAssetAttribute"/> id planes — id 100 here does not
    /// collide with id 100 elsewhere.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class KlothoComponentAttribute : Attribute
    {
        public const int UserMinId = 100;

        public int ComponentTypeId { get; }
        public KlothoComponentAttribute(int componentTypeId) => ComponentTypeId = componentTypeId;
    }
}
