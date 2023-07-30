using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Serialization;
using LiteNetLibManager;
using LiteNetLib;
using Cysharp.Threading.Tasks;
using UnityEngine.Events;


namespace MultiplayerARPG
{
    public class HarvestableMonsterCharacterEntity : MonsterCharacterEntity
    {
        public override void Killed(EntityInfo lastAttacker)
        {
            base.Killed(lastAttacker);

            // If this summoned by someone, don't give reward to killer
            if (IsSummoned)
                return;

            Reward reward = CurrentGameplayRule.MakeMonsterReward(CharacterDatabase, Level);
            // Temp data which will be in-use in loop
            BaseCharacterEntity tempCharacterEntity;
            BasePlayerCharacterEntity tempPlayerCharacterEntity;
            BaseMonsterCharacterEntity tempMonsterCharacterEntity;
            // Last player is last player who kill the monster
            // Whom will have permission to pickup an items before other
            BasePlayerCharacterEntity lastPlayer = null;
            BaseCharacterEntity attackerCharacter;
            if (lastAttacker.TryGetEntity(out attackerCharacter))
            {
                if (attackerCharacter is BaseMonsterCharacterEntity)
                {
                    tempMonsterCharacterEntity = attackerCharacter as BaseMonsterCharacterEntity;
                    if (tempMonsterCharacterEntity.Summoner != null &&
                        tempMonsterCharacterEntity.Summoner is BasePlayerCharacterEntity)
                    {
                        // Set its summoner as main enemy
                        lastAttacker = tempMonsterCharacterEntity.Summoner.GetInfo();
                        lastAttacker.TryGetEntity(out attackerCharacter);
                    }
                }
                lastPlayer = attackerCharacter as BasePlayerCharacterEntity;
            }
            GuildData tempGuildData;
            PartyData tempPartyData;
            bool givenRewardExp;
            bool givenRewardCurrency;
            float shareGuildExpRate;
            if (receivedDamageRecords.Count > 0)
            {
                float tempHighRewardRate = 0f;
                foreach (BaseCharacterEntity enemy in receivedDamageRecords.Keys)
                {
                    if (enemy == null)
                        continue;

                    tempCharacterEntity = enemy;
                    givenRewardExp = false;
                    givenRewardCurrency = false;
                    shareGuildExpRate = 0f;

                    ReceivedDamageRecord receivedDamageRecord = receivedDamageRecords[tempCharacterEntity];
                    float rewardRate = (float)receivedDamageRecord.totalReceivedDamage / (float)this.GetCaches().MaxHp;
                    if (rewardRate > 1f)
                        rewardRate = 1f;

                    if (tempCharacterEntity is BaseMonsterCharacterEntity)
                    {
                        tempMonsterCharacterEntity = tempCharacterEntity as BaseMonsterCharacterEntity;
                        if (tempMonsterCharacterEntity.Summoner != null &&
                            tempMonsterCharacterEntity.Summoner is BasePlayerCharacterEntity)
                        {
                            // Set its summoner as main enemy
                            tempCharacterEntity = tempMonsterCharacterEntity.Summoner;
                        }
                    }

                    if (tempCharacterEntity is BasePlayerCharacterEntity)
                    {
                        bool makeMostDamage = false;
                        tempPlayerCharacterEntity = tempCharacterEntity as BasePlayerCharacterEntity;
                        // Clear looters list when it is found new player character who make most damages
                        if (rewardRate > tempHighRewardRate)
                        {
                            tempHighRewardRate = rewardRate;
                            looters.Clear();
                            makeMostDamage = true;
                        }
                        // Try find guild data from player character
                        if (tempPlayerCharacterEntity.GuildId > 0 && GameInstance.ServerGuildHandlers.TryGetGuild(tempPlayerCharacterEntity.GuildId, out tempGuildData))
                        {
                            // Calculation amount of Exp which will be shared to guild
                            shareGuildExpRate = (float)tempGuildData.ShareExpPercentage(tempPlayerCharacterEntity.Id) * 0.01f;
                            // Will share Exp to guild when sharing amount more than 0
                            if (shareGuildExpRate > 0)
                            {
                                // Increase guild exp
                                GameInstance.ServerGuildHandlers.IncreaseGuildExp(tempPlayerCharacterEntity, (int)(reward.exp * shareGuildExpRate * rewardRate));
                            }
                        }
                        // Try find party data from player character
                        if (tempPlayerCharacterEntity.PartyId > 0 && GameInstance.ServerPartyHandlers.TryGetParty(tempPlayerCharacterEntity.PartyId, out tempPartyData))
                        {
                            List<BasePlayerCharacterEntity> sharingExpMembers = new List<BasePlayerCharacterEntity>();
                            List<BasePlayerCharacterEntity> sharingItemMembers = new List<BasePlayerCharacterEntity>();
                            BasePlayerCharacterEntity nearbyPartyMember;
                            foreach (string memberId in tempPartyData.GetMemberIds())
                            {
                                if (GameInstance.ServerUserHandlers.TryGetPlayerCharacterById(memberId, out nearbyPartyMember) && !nearbyPartyMember.IsDead())
                                {
                                    if (tempPartyData.shareExp)
                                    {
                                        if (GameInstance.Singleton.partyShareExpDistance <= 0f || Vector3.Distance(tempPlayerCharacterEntity.CacheTransform.position, nearbyPartyMember.CacheTransform.position) <= GameInstance.Singleton.partyShareExpDistance)
                                            sharingExpMembers.Add(nearbyPartyMember);
                                    }
                                    if (tempPartyData.shareItem)
                                    {
                                        if (GameInstance.Singleton.partyShareItemDistance <= 0f || Vector3.Distance(tempPlayerCharacterEntity.CacheTransform.position, nearbyPartyMember.CacheTransform.position) <= GameInstance.Singleton.partyShareItemDistance)
                                            sharingItemMembers.Add(nearbyPartyMember);
                                    }
                                }
                            }
                            int countNearbyPartyMembers;
                            // Share EXP to party members
                            countNearbyPartyMembers = sharingExpMembers.Count;
                            for (int i = 0; i < sharingExpMembers.Count; ++i)
                            {
                                nearbyPartyMember = sharingExpMembers[i];
                                // If share exp, every party member will receive devided exp
                                // If not share exp, character who make damage will receive non-devided exp
                                nearbyPartyMember.RewardExp(reward, (1f - shareGuildExpRate) / (float)countNearbyPartyMembers * rewardRate, RewardGivenType.PartyShare);
                            }
                            // Share Items to party members
                            countNearbyPartyMembers = sharingItemMembers.Count;
                            for (int i = 0; i < sharingItemMembers.Count; ++i)
                            {
                                nearbyPartyMember = sharingItemMembers[i];
                                // If share item, every party member will receive devided gold
                                // If not share item, character who make damage will receive non-devided gold
                                if (makeMostDamage)
                                {
                                    // Make other member in party able to pickup items
                                    looters.Add(nearbyPartyMember.Id);
                                }
                                nearbyPartyMember.RewardCurrencies(reward, 1f / (float)countNearbyPartyMembers * rewardRate, RewardGivenType.PartyShare);
                            }
                            // Shared exp has been given, so do not give it to character again
                            if (tempPartyData.shareExp)
                                givenRewardExp = true;
                            // Shared gold has been given, so do not give it to character again
                            if (tempPartyData.shareItem)
                                givenRewardCurrency = true;
                        }

                        // Add reward to current character in damage record list
                        if (!givenRewardExp)
                        {
                            // Will give reward when it was not given
                            int petIndex = tempPlayerCharacterEntity.IndexOfSummon(SummonType.PetItem);
                            if (petIndex >= 0)
                            {
                                tempMonsterCharacterEntity = tempPlayerCharacterEntity.Summons[petIndex].CacheEntity;
                                if (tempMonsterCharacterEntity != null)
                                {
                                    // Share exp to pet, set multiplier to 0.5, because it will be shared to player
                                    tempMonsterCharacterEntity.RewardExp(reward, (1f - shareGuildExpRate) * 0.5f * rewardRate, RewardGivenType.KillMonster);
                                }
                                // Set multiplier to 0.5, because it was shared to monster
                                tempPlayerCharacterEntity.RewardExp(reward, (1f - shareGuildExpRate) * 0.5f * rewardRate, RewardGivenType.KillMonster);
                            }
                            else
                            {
                                // No pet, no share, so rate is 1f
                                tempPlayerCharacterEntity.RewardExp(reward, (1f - shareGuildExpRate) * rewardRate, RewardGivenType.KillMonster);
                            }
                        }

                        if (!givenRewardCurrency)
                        {
                            // Will give reward when it was not given
                            tempPlayerCharacterEntity.RewardCurrencies(reward, rewardRate, RewardGivenType.KillMonster);
                        }

                        if (makeMostDamage)
                        {
                            // Make current character able to pick up item because it made most damage
                            looters.Add(tempPlayerCharacterEntity.Id);
                        }
                    }   // End is `BasePlayerCharacterEntity` condition
                }   // End for-loop
            }   // End count recived damage record count
            receivedDamageRecords.Clear();
            // Clear dropping items, it will fills in `OnRandomDropItem` function
            droppingItems.Clear();
            // Drop items
            CharacterDatabase.RandomItems(OnRandomDropItem);

            switch (CurrentGameInstance.monsterDeadDropItemMode)
            {
                case DeadDropItemMode.DropOnGround:
                    for (int i = 0; i < droppingItems.Count; ++i)
                    {
                        ItemDropEntity.DropItem(this, droppingItems[i], looters);
                    }
                    break;
                case DeadDropItemMode.CorpseLooting:
                    if (droppingItems.Count > 0)
                        ItemsContainerEntity.DropItems(CurrentGameInstance.monsterCorpsePrefab, this, droppingItems, looters, CurrentGameInstance.monsterCorpseAppearDuration);
                    break;

                case DeadDropItemMode.CorpseHarvest:
                    if (droppingItems.Count > 0)
                    //ItemsContainerEntity.DropItems(CurrentGameInstance.monsterCorpsePrefab, this, droppingItems, looters, CurrentGameInstance.monsterCorpseAppearDuration);

                    // STG
                    {
                        ItemsContainerEntity dropped = ItemsContainerEntity.DropItems(CurrentGameInstance.monsterCorpsePrefab, this, droppingItems, looters, CurrentGameInstance.monsterCorpseAppearDuration);
                        dropped.transform.parent = transform;
                    }

                    break;
            }

            if (lastPlayer != null)
            {
                // Increase kill progress
                lastPlayer.OnKillMonster(this);
            }

            if (!IsSummoned)
            {
                // If not summoned by someone, destroy and respawn it
                DestroyAndRespawn();
            }

            // Clear looters because they are already set to dropped items
            looters.Clear();
        }

        private void OnRandomDropItem(BaseItem item, short amount)
        {
            // Drop item to the ground
            if (amount > item.MaxStack)
                amount = item.MaxStack;
            droppingItems.Add(CharacterItem.Create(item, 1, amount));
        }

        [Category(5, "Harvestable Settings")]
        [SerializeField]
        protected int maxHp = 100;

        [SerializeField]
        protected Harvestable harvestable;

        [SerializeField]
        protected HarvestableCollectType collectType;

        [SerializeField]
        [Tooltip("Radius to detect other entities to avoid spawn this harvestable nearby other entities")]
        protected float colliderDetectionRadius = 2f;

        [Category("Events")]
        [SerializeField]
        protected UnityEvent onHarvestableDestroy = new UnityEvent();

        public override string EntityTitle
        {
            get
            {
                string title = base.EntityTitle;
                return !string.IsNullOrEmpty(title) ? title : harvestable.Title;
            }
        }
        public float ColliderDetectionRadius { get { return colliderDetectionRadius; } }

        public override void PrepareRelatesData()
        {
            base.PrepareRelatesData();
            GameInstance.AddHarvestables(harvestable);
        }

        protected override void EntityAwake()
        {
            base.EntityAwake();
            gameObject.tag = CurrentGameInstance.harvestableTag;
            gameObject.layer = CurrentGameInstance.harvestableLayer;
            isStaticHitBoxes = true;
            isDestroyed = false;
        }

        public override void OnSetup()
        {
            base.OnSetup();
            // Initial default data
            InitStats();
            if (SpawnArea == null)
                SpawnPosition = CacheTransform.position;
        }

        [AllRpc]
        protected virtual void AllOnHarvestableDestroy()
        {
            if (onHarvestableDestroy != null)
                onHarvestableDestroy.Invoke();
        }

        public void CallAllOnHarvestableDestroy()
        {
            RPC(AllOnHarvestableDestroy);
        }

        protected override void ApplyReceiveDamage(HitBoxPosition position, Vector3 fromPosition, EntityInfo instigator, Dictionary<DamageElement, MinMaxFloat> damageAmounts, CharacterItem weapon, BaseSkill skill, short skillLevel, int randomSeed, out CombatAmountType combatAmountType, out int totalDamage)
        {
            BaseCharacterEntity attackerCharacter;
            instigator.TryGetEntity(out attackerCharacter);
            // Apply damages, won't apply skill damage
            float calculatingTotalDamage = 0f;
            // Harvest type is based on weapon by default
            HarvestType skillHarvestType = HarvestType.BasedOnWeapon;
            if (skill != null && skillLevel > 0)
            {
                skillHarvestType = skill.GetHarvestType();
            }
            // Get randomizer and random damage
            WeightedRandomizer<ItemDropByWeight> itemRandomizer = null;
            switch (skillHarvestType)
            {
                case HarvestType.BasedOnWeapon:
                    {
                        IWeaponItem weaponItem = weapon.GetWeaponItem();
                        HarvestEffectiveness harvestEffectiveness;
                        if (harvestable.CacheHarvestEffectivenesses.TryGetValue(weaponItem.WeaponType, out harvestEffectiveness) &&
                            harvestable.CacheHarvestItems.TryGetValue(weaponItem.WeaponType, out itemRandomizer))
                        {
                            calculatingTotalDamage = weaponItem.HarvestDamageAmount.GetAmount(weapon.level).Random(randomSeed) * harvestEffectiveness.damageEffectiveness;
                        }
                    }
                    break;
                case HarvestType.BasedOnSkill:
                    {
                        SkillHarvestEffectiveness skillHarvestEffectiveness;
                        if (harvestable.CacheSkillHarvestEffectivenesses.TryGetValue(skill, out skillHarvestEffectiveness) &&
                            harvestable.CacheSkillHarvestItems.TryGetValue(skill, out itemRandomizer))
                        {
                            calculatingTotalDamage = skill.GetHarvestDamageAmount().GetAmount(skillLevel).Random(randomSeed) * skillHarvestEffectiveness.damageEffectiveness;
                        }
                    }
                    break;
            }
            // If found randomizer, random dropping items
            if (skillHarvestType != HarvestType.None && itemRandomizer != null)
            {
                ItemDropByWeight receivingItem = itemRandomizer.TakeOne();
                int itemDataId = receivingItem.item.DataId;
                short itemAmount = (short)(receivingItem.amountPerDamage * calculatingTotalDamage);
                bool droppingToGround = collectType == HarvestableCollectType.DropToGround;

                if (attackerCharacter != null)
                {
                    if (attackerCharacter.IncreasingItemsWillOverwhelming(itemDataId, itemAmount))
                        droppingToGround = true;
                    if (!droppingToGround)
                    {
                        GameInstance.ServerGameMessageHandlers.NotifyRewardItem(attackerCharacter.ConnectionId, itemDataId, itemAmount);
                        attackerCharacter.IncreaseItems(CharacterItem.Create(itemDataId, 1, itemAmount));
                        attackerCharacter.FillEmptySlots();
                    }
                    attackerCharacter.RewardExp(new Reward() { exp = (int)(harvestable.expPerDamage * calculatingTotalDamage) }, 1, RewardGivenType.Harvestable);
                }
                else
                {
                    // Attacker is not character, always drop item to ground
                    droppingToGround = true;
                }

                if (droppingToGround)
                    ItemDropEntity.DropItem(this, CharacterItem.Create(itemDataId, 1, itemAmount), new string[0]);
            }
            // Apply damages
            combatAmountType = CombatAmountType.NormalDamage;
            totalDamage = CurrentGameInstance.GameplayRule.GetTotalDamage(fromPosition, instigator, this, calculatingTotalDamage, weapon, skill, skillLevel);
            if (totalDamage < 0)
                totalDamage = 0;
            CurrentHp -= totalDamage;
        }

        protected async UniTaskVoid RespawnRoutine(float delay)
        {
            await UniTask.Delay(Mathf.CeilToInt(delay * 1000));
            InitStats();
            Manager.Assets.NetworkSpawnScene(
                Identity.ObjectId,
                SpawnPosition,
                CurrentGameInstance.DimensionType == DimensionType.Dimension3D ? Quaternion.Euler(Vector3.up * Random.Range(0, 360)) : Quaternion.identity);
        }

        public override bool CanReceiveDamageFrom(EntityInfo entityInfo)
        {
            // Harvestable entity can receive damage inside safe area
            return true;
        }

#if UNITY_EDITOR
        protected override void OnDrawGizmosSelected()
        {
            base.OnDrawGizmos();
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(transform.position, colliderDetectionRadius);
        }
#endif
    }

}
