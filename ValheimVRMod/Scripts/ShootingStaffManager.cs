using UnityEngine;
using ValheimVRMod.Utilities;

namespace ValheimVRMod.Scripts
{
    // Manages staves that always aim-and-shoot their projectile (e.g. staff of frost, Dundr).
    public class ShootingStaffManager : LocalWeaponWield, IMagicStaffManager
    {
        public static ShootingStaffManager instance;

        protected override void Awake()
        {
            base.Awake();
            instance = this;
        }

        protected override void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
            base.OnDestroy();
        }

        public bool AttemptingAttack
        {
            get
            {
                if (VHVRConfig.GestureMagicCasting() && MagicGestureChargeController.instance != null)
                {
                    if (!MagicStaffUtils.IsShootingTriggerHeld())
                    {
                        return false;
                    }
                    var attack = Player.m_localPlayer?.GetRightItem()?.m_shared?.m_attack;
                    return MagicGestureChargeController.instance.OnAttemptAttack(attack);
                }
                return MagicStaffUtils.IsShootingTriggerHeld();
            }
        }

        public bool IsSecondaryAttack
        {
            get { return MagicStaffUtils.IsSecondaryAttack(); }
        }

        public bool TrySecondaryAttack
        {
            get { return MagicStaffUtils.TrySecondaryAttack(); }
        }

        public Vector3 AimDir
        {
            get
            {
                return (VHVRConfig.GestureMagicCasting() || isCurrentlyTwoHanded() || isAiming) ?
                    weaponForward :
                    MagicStaffUtils.WeaponHandPointer.rayDirection * Vector3.forward;
            }
        }

        public Vector3 GetProjectileSpawnPoint(Attack attack)
        {
            return MagicStaffUtils.GetProjectileSpawnPoint(attack, weaponForward.normalized, MagicStaffUtils.WeaponHandPointer);
        }
    }
}
