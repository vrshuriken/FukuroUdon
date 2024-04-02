﻿/*
Copyright (c) 2023 Mimy Quality
Released under the MIT license
https://opensource.org/licenses/mit-license.php
*/

namespace MimyLab
{
    using UdonSharp;
    using UnityEngine;
    using VRC.SDKBase;
    using VRC.SDK3.Components;
    //using VRC.Udon;

#if UNITY_EDITOR
    using UnityEditor;
#if UNITY_2021_2_OR_NEWER
    using UnityEditor.SceneManagement;
#else
    using UnityEditor.Experimental.SceneManagement;
#endif
    using UdonSharpEditor;
#endif

    [AddComponentMenu("Fukuro Udon/Manual ObjectSync/Manual ObjectSync")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ManualObjectSync : UdonSharpBehaviour
    {
        [Header("Settings")]
        [Tooltip("The tick rate depends on the fps of this game object owner's client.")]
        [Min(2)]
        public int moveCheckTickRate = 30;  // 変動確認の周期(fps依存)
        public Space moveCheckSpace = Space.Self;    // 変動確認をローカル座標系でするか

        [Header("Option Settings")]
        public Transform attachPoint = null;   // アタッチモード時の追従先

        public bool UseGravity  // Rigidbody.useGravity同期用
        {
            get => _useGravity;
            set
            {
                Initialize();

                _useGravity = value;
                if (_rigidbody) { _rigidbody.useGravity = value; }
                RequestSerialization();
            }
        }
        public bool IsKinematic // Rigidbody.isKinematic同期用
        {
            get => _isKinematic;
            set
            {
                Initialize();

                _isKinematic = value;
                if (_rigidbody) { _rigidbody.isKinematic = (_isAttached || _isEquiped || !Networking.IsOwner(this.gameObject)) ? true : value; }
                RequestSerialization();
            }
        }

        public bool Pickupable  // VRCPickup.pickupable同期用
        {
            get => _pickupable;
            set
            {
                Initialize();

                _pickupable = value;
                if (_pickup) { _pickup.pickupable = (_pickup.DisallowTheft && _isHeld && !Networking.IsOwner(this.gameObject)) ? false : value; }
                RequestSerialization();
            }
        }
        public bool IsHeld  // ピックアップ中か(自他問わず)
        {
            get => _isHeld;
        }
        private void SetIsHeld(bool value)
        {
            Initialize();

            _isHeld = value;
            if (value)
            {
                _isEquiped = false;
                _isAttached = false;
                _updateManager.EnablePostLateUpdate(this);
            }
            if (_pickup) { Pickupable = Pickupable; }
            if (_rigidbody) { IsKinematic = IsKinematic; }
            RequestSerialization();
        }

        public VRCPickup.PickupHand PickupHand    // ピックアップしてる方の手
        {
            get
            {
                if (_isHeld)
                {
                    if (_equipBone == (byte)HumanBodyBones.LeftHand) { return VRCPickup.PickupHand.Left; }
                    if (_equipBone == (byte)HumanBodyBones.RightHand) { return VRCPickup.PickupHand.Right; }
                }
                return VRCPickup.PickupHand.None;
            }
        }

        public bool IsEquiped   // ボーンに装着モード
        {
            get => _isEquiped;
        }
        private void SetIsEquiped(bool value)
        {
            Initialize();

            _isEquiped = value;
            if (value)
            {
                _isAttached = false;
                if (_pickup && _pickup.IsHeld) { _pickup.Drop(); }
                _updateManager.EnablePostLateUpdate(this);
            }
            if (_rigidbody) { IsKinematic = IsKinematic; }
            RequestSerialization();
        }

        public bool IsAttached  // アタッチモード
        {
            get => _isAttached;
        }
        private void SetIsAttached(bool value)
        {
            Initialize();

            _isAttached = value;
            if (value)
            {
                _isEquiped = false;
                if (_pickup && _pickup.IsHeld) { _pickup.Drop(); }
                _updateManager.EnablePostLateUpdate(this);
            }
            if (_rigidbody) { IsKinematic = IsKinematic; }
            RequestSerialization();
        }

        private readonly string _UpdateManagerPrefabGUID = "51374f5e01425074ca9cb544fa44007d";
        [HideInInspector]
        public MOSUpdateManager _updateManager = null;
        [HideInInspector]
        public float _respawnHightY = -100.0f;   // ここより落下したらリスポーンする

        [UdonSynced] Vector3 _syncPosition = Vector3.zero; // 位置同期用、ピックアップ時はオフセット用
        [UdonSynced] Quaternion _syncRotation = Quaternion.identity; // 回転同期用、ピックアップ時はオフセット用
        [UdonSynced] Vector3 _syncScale = Vector3.one;    // 拡縮同期用

        [FieldChangeCallback(nameof(UseGravity))]
        [UdonSynced] bool _useGravity = false;
        [FieldChangeCallback(nameof(IsKinematic))]
        [UdonSynced] bool _isKinematic = true;
        [FieldChangeCallback(nameof(Pickupable))]
        [UdonSynced] bool _pickupable = true;
        [FieldChangeCallback(nameof(IsHeld))]
        [UdonSynced] bool _isHeld = false;
        [UdonSynced] byte _equipBone = (byte)VRCPickup.PickupHand.None;

        [FieldChangeCallback(nameof(IsEquiped))]
        [UdonSynced] bool _isEquiped = false;

        [FieldChangeCallback(nameof(IsAttached))]
        [UdonSynced] bool _isAttached = false;

        // 初期値保存用
        Vector3 _startPosition, _localPosition;
        Quaternion _startRotation, _localRotation;
        Vector3 _startScale = Vector3.one, _localScale = Vector3.one;

        // 計算用
        Rigidbody _rigidbody = null;
        VRCPickup _pickup = null;
        VRCPlayerApi _localPlayer, _ownerPlayer;
        int _firstCheckTiming;
        bool _reservedInterval = false;
        bool _syncHasChanged = false;

#if !COMPILER_UDONSHARP && UNITY_EDITOR
        private void OnValidate()
        {
            EditorApplication.delayCall += () => { if (this) this.MakeUpdateManager(); };
        }

        private void MakeUpdateManager()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { return; }
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) { return; }
            if (PrefabUtility.IsPartOfPrefabAsset(this)) { return; }

            if (_updateManager) { return; }
            if (_updateManager = FindObjectOfType<MOSUpdateManager>())
            {
                _respawnHightY = _updateManager.respawnHeightY;
                RecordSelf();
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(_UpdateManagerPrefabGUID));
            if (!prefab)
            {
                Debug.LogError("MOSUpdateManager prefab could not be loaded.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Created MOSUpdateManager prefab");
            Debug.Log("Created MOSUpdateManager prefab");

            instance.GetComponentInChildren<MOSUpdateManager>().SetupAllMOS();
        }

        internal void SetUpdateManager(MOSUpdateManager um)
        {
            _updateManager = um;
        }

        internal void SetRespawnHeightY(float y)
        {
            _respawnHightY = y;
        }

        internal void RecordSelf()
        {
            UdonSharpEditorUtility.CopyProxyToUdon(this);
            EditorUtility.SetDirty(this);
            PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        }
#endif

        bool _initialized = false;
        private void Initialize()
        {
            if (_initialized) { return; }

            _rigidbody = GetComponent<Rigidbody>();
            _pickup = GetComponent<VRCPickup>();
            _localPlayer = Networking.LocalPlayer;
            _ownerPlayer = Networking.GetOwner(this.gameObject);

            _startPosition = transform.position;
            _startRotation = transform.rotation;
            _startScale = transform.localScale;
            _localPosition = transform.localPosition;
            _localRotation = transform.localRotation;
            _localScale = transform.localScale;

            moveCheckTickRate = Mathf.Max(moveCheckTickRate, 2);
            _firstCheckTiming = moveCheckTickRate + GetInstanceID() % moveCheckTickRate;

            if (_rigidbody)
            {
                _useGravity = _rigidbody.useGravity;
                _isKinematic = _rigidbody.isKinematic;
            }
            if (_pickup)
            {
                _pickupable = _pickup.pickupable;
            }

            if (_syncPosition.Equals(Vector3.zero)
            && _syncRotation.Equals(Quaternion.identity)
            && _syncScale.Equals(Vector3.one))
            {
                // _sync系が全部初期値ならInitialize時点では同期されてきてないと見なして初期化
                _syncPosition = _startPosition;
                _syncRotation = _startRotation;
                _syncScale = _startScale;
            }

            _initialized = true;
        }
        private void Start()
        {
            Initialize();

            UseGravity = UseGravity;
            IsKinematic = IsKinematic;
            Pickupable = Pickupable;

            if (Networking.IsOwner(this.gameObject))
            {
                if (_reservedInterval) { return; }

                _reservedInterval = true;
                SendCustomEventDelayedFrames(nameof(_IntervalPostLateUpdate), _firstCheckTiming);
            }
        }

        public void _OnPostLateUpdate()
        {
            if (AttachToTransform()) { return; }

            if (EquipBone()) { return; }

            if (Networking.IsOwner(this.gameObject))
            {
                if (!PickupOffsetCheck())
                {
                    TransformMoveCheck();
                }
            }
            else
            {
                if (HoldingOther()) { return; }

                ApplySyncTransform();
            }

            _updateManager.DisablePostLateUpdate(this);
        }

        public void _IntervalPostLateUpdate()
        {
            _reservedInterval = false;

            if (Networking.IsOwner(this.gameObject))
            {
                _updateManager.EnablePostLateUpdate(this);
                _reservedInterval = true;
                SendCustomEventDelayedFrames(nameof(_IntervalPostLateUpdate), moveCheckTickRate);
            }
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            Initialize();

            _ownerPlayer = player;

            if (player.isLocal && !_reservedInterval)
            {
                _reservedInterval = true;
                SendCustomEventDelayedFrames(nameof(_IntervalPostLateUpdate), _firstCheckTiming);
            }

            // Ownerだった人が落ちた対策に、いったん強制解除
            if (_isEquiped) { SetIsEquiped(false); }

            if (_pickup)
            {
                if (player.isLocal)
                {
                    // 自分がOwner化＝ピックアップを奪ったか、Ownerだった人が落ちた
                    if (!_pickup.IsHeld)
                    {
                        SetIsHeld(false);
                    }
                }
                else
                {
                    // 他人がOwner化＝ピックアップを奪われた
                    _pickup.Drop();
                }
            }

            // Ownerに物理演算書き戻し
            if (_rigidbody)
            {
                _rigidbody.isKinematic = player.isLocal ? IsKinematic : true;
            }
        }

        public override void OnDeserialization()
        {
            Initialize();

            // 本当に_syncPosition/Rotation/Scaleに変化があったかは見ない
            _syncHasChanged = true;
            _updateManager.EnablePostLateUpdate(this);
        }

        // VRCPickupとRigidbodyがある
        public override void OnPickup()
        {
            Networking.SetOwner(_localPlayer, this.gameObject);
            SetIsHeld(true);

            PickupOffsetCheck();
        }

        // VRCPickupとRigidbodyがある
        public override void OnDrop()
        {
            SetIsHeld(false);

            _syncPosition = transform.position;
            _syncRotation = transform.rotation;
            _localPosition = transform.localPosition;
            _localRotation = transform.localRotation;

            RequestSerialization();

            transform.hasChanged = false;
        }

        public void Respawn()
        {
            Initialize();

            if (Networking.IsOwner(this.gameObject))
            {
                if (_pickup) { _pickup.Drop(); }
                SetIsEquiped(false);
                SetIsAttached(false);

                if (_rigidbody)
                {
                    _rigidbody.velocity = Vector3.zero;
                    _rigidbody.angularVelocity = Vector3.zero;
                    _rigidbody.position = _startPosition;
                    _rigidbody.rotation = _startRotation;
                }
                else
                {
                    transform.SetPositionAndRotation(_startPosition, _startRotation);
                }

                _syncPosition = _startPosition;
                _syncRotation = _startRotation;
                _localPosition = transform.localPosition;
                _localRotation = transform.localRotation;

                RequestSerialization();

                transform.hasChanged = false;
            }
        }

        public void ResetScale()
        {
            Initialize();

            if (Networking.IsOwner(this.gameObject))
            {
                transform.localScale = _startScale;

                _syncScale = _startScale;
                _localScale = transform.localScale;

                RequestSerialization();

                transform.hasChanged = false;
            }
        }

        public void Equip(HumanBodyBones targetBone)
        {
            if (!Networking.IsOwner(this.gameObject)) { return; }

            SetIsEquiped(true);

            _equipBone = (byte)targetBone;
            var bonePosition = _localPlayer.GetBonePosition(targetBone);
            var boneRotation = _localPlayer.GetBoneRotation(targetBone);
            _syncPosition = bonePosition.Equals(Vector3.zero) ? Vector3.zero : Quaternion.Inverse(boneRotation) * (transform.position - bonePosition);
            _syncRotation = boneRotation.Equals(Quaternion.identity) ? Quaternion.identity : (Quaternion.Inverse(boneRotation) * transform.rotation);

            RequestSerialization();
        }
        public void Unequip()
        {
            if (Networking.IsOwner(this.gameObject)) { SetIsEquiped(false); }
        }

        public void Attach()
        {
            if (Networking.IsOwner(this.gameObject)) { SetIsAttached(true); }
        }
        public void Detach()
        {
            if (Networking.IsOwner(this.gameObject)) { SetIsAttached(false); }
        }

        private bool TransformMoveCheck()
        {
            if (!transform.hasChanged) { return false; }

            if (transform.position.y <= _respawnHightY)
            {
                Respawn();
                return true;
            }

            if (moveCheckSpace == Space.Self
            && (transform.localPosition != _localPosition
             || transform.localRotation != _localRotation))
            {
                SyncLocation();
            }
            else if (moveCheckSpace == Space.World
            && (transform.position != _syncPosition
             || transform.rotation != _syncRotation))
            {
                SyncLocation();
            }

            if (transform.localScale != _localScale)
            {
                SyncScale();
            }

            transform.hasChanged = false;

            return true;
        }
        private void SyncLocation()
        {
            _syncPosition = transform.position;
            _syncRotation = transform.rotation;
            _localPosition = transform.localPosition;
            _localRotation = transform.localRotation;

            RequestSerialization();
        }
        private void SyncScale()
        {
            _syncScale = transform.localScale;
            _localScale = transform.localScale;

            RequestSerialization();
        }

        private bool ApplySyncTransform()
        {
            if (!_syncHasChanged) { return _syncHasChanged; }

            if (_rigidbody)
            {
                _rigidbody.MovePosition(_syncPosition);
                _rigidbody.MoveRotation(_syncRotation);
            }
            else
            {
                transform.SetPositionAndRotation(_syncPosition, _syncRotation);
            }
            var parent = transform.parent;
            _localPosition = parent ? transform.InverseTransformPoint(_syncPosition) : _syncPosition;
            _localRotation = parent ? Quaternion.Inverse(parent.rotation) * _syncRotation : _syncRotation;

            if (transform.localScale != _syncScale)
            {
                transform.localScale = _syncScale;
                _localScale = _syncScale;
            }

            _syncHasChanged = false;

            return true;
        }

        // _isHeldならVRCPickupとRigidbodyが付いている
        private bool PickupOffsetCheck()
        {
            if (!_isHeld) { return false; }

            var pickupHandBone = (_pickup.currentHand == VRCPickup.PickupHand.Left) ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            if (_equipBone != (byte)pickupHandBone)
            {
                _equipBone = (byte)pickupHandBone;
                RequestSerialization();
            }

            var handPosition = _localPlayer.GetBonePosition(pickupHandBone);
            var handRotation = _localPlayer.GetBoneRotation(pickupHandBone);

            var offsetPosition = handPosition.Equals(Vector3.zero) ? Vector3.zero : Quaternion.Inverse(handRotation) * (_rigidbody.position - handPosition);
            var offsetRotation = handRotation.Equals(Quaternion.identity) ? Quaternion.identity : (Quaternion.Inverse(handRotation) * _rigidbody.rotation);

            if (offsetPosition != _syncPosition
             || offsetRotation != _syncRotation)
            {
                _syncPosition = offsetPosition;
                _syncRotation = offsetRotation;

                RequestSerialization();
            }

            return true;
        }

        // _isHeldならVRCPickupとRigidbodyが付いている
        private bool HoldingOther()
        {
            if (!_isHeld) { return false; }
            if (!Utilities.IsValid(_ownerPlayer)) { return true; }

            var pickupHandBone = (_equipBone == (byte)HumanBodyBones.LeftHand) ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            var handPosition = _ownerPlayer.GetBonePosition(pickupHandBone);
            var handRotation = _ownerPlayer.GetBoneRotation(pickupHandBone);

            if (handPosition.Equals(Vector3.zero)
             || handRotation.Equals(Quaternion.identity))
            {
                // ボーン情報の代わりにプレイヤー原点からの固定値
                handPosition = new Vector3((_equipBone == (byte)HumanBodyBones.LeftHand) ? -0.2f : 0.2f, 1.0f, 0.3f);
                _rigidbody.MovePosition(_ownerPlayer.GetPosition() + (_ownerPlayer.GetRotation() * handPosition));
                _rigidbody.MoveRotation(_ownerPlayer.GetRotation());
            }
            else
            {
                _rigidbody.MovePosition(handPosition + (handRotation * _syncPosition));
                _rigidbody.MoveRotation(handRotation * _syncRotation);
            }

            return true;
        }

        private bool EquipBone()
        {
            if (!_isEquiped) { return false; }
            if (!Utilities.IsValid(_ownerPlayer)) { return true; }

            var bonePosition = _ownerPlayer.GetBonePosition((HumanBodyBones)_equipBone);
            var boneRotation = _ownerPlayer.GetBoneRotation((HumanBodyBones)_equipBone);
            if (bonePosition.Equals(Vector3.zero) || boneRotation.Equals(Quaternion.identity)) { return _isEquiped; }

            var equipPosition = bonePosition + (boneRotation * _syncPosition);
            var equipRotation = boneRotation * _syncRotation;

            transform.SetPositionAndRotation(equipPosition, equipRotation);
            _syncHasChanged = false;

            return true;
        }

        private bool AttachToTransform()
        {
            if (!_isAttached) { return false; }

            transform.SetPositionAndRotation(attachPoint.position, attachPoint.rotation);
            _syncHasChanged = false;

            return true;
        }
    }
}
