using UnityEngine;

using xpTURN.Klotho;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// EntityViewFactory implementation for the Brawler sample.
    /// Provides only the game-specific decisions (which entities to render, which prefab to use).
    /// BindBehaviour / ViewFlags / Pool integration are handled by the base class.
    /// </summary>
    [CreateAssetMenu(menuName = "Brawler/EntityViewFactory", fileName = "BrawlerEntityViewFactory")]
    public class BrawlerEntityViewFactory : EntityViewFactory
    {
        [Header("Character Prefabs (CharacterClass index)")]
        [Tooltip("[0]=Warrior  [1]=Mage  [2]=Rogue  [3]=Knight")]
        [SerializeField] private GameObject[] _characterPrefabs;

        [Header("Item Prefabs (ItemType index)")]
        [Tooltip("[0]=Shield  [1]=Boost  [2]=Bomb")]
        [SerializeField] private GameObject[] _itemPrefabs;

        protected override bool ShouldRender(Frame frame, EntityRef entity)
        {
            return frame.Has<CharacterComponent>(entity) || frame.Has<ItemComponent>(entity);
        }

        protected override GameObject ResolvePrefab(Frame frame, EntityRef entity)
        {
            if (frame.Has<CharacterComponent>(entity) && _characterPrefabs != null && _characterPrefabs.Length > 0)
            {
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
                int idx = Mathf.Clamp(c.CharacterClass, 0, _characterPrefabs.Length - 1);
                return _characterPrefabs[idx];
            }
            if (frame.Has<ItemComponent>(entity) && _itemPrefabs != null && _itemPrefabs.Length > 0)
            {
                ref readonly var i = ref frame.GetReadOnly<ItemComponent>(entity);
                int idx = Mathf.Clamp(i.ItemType, 0, _itemPrefabs.Length - 1);
                return _itemPrefabs[idx];
            }
            return null;
        }
    }
}
