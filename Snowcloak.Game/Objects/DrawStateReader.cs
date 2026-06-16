using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System.Runtime.CompilerServices;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using VisibilityFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags;

namespace Snowcloak.Game.Objects;

public static unsafe class DrawStateReader
{
    public const int CustomizeDataLength = 26;
    public const int EquipmentDataLength = 40;
    public const int WeaponDataLength = 3;

    public static CharacterObjectState ReadCharacterObject(
        nint address,
        bool inspectModelSlots,
        Span<byte> equipmentData,
        Span<ushort> mainHandData,
        Span<ushort> offHandData,
        Span<byte> customizeData)
    {
        ValidateBuffer(equipmentData, EquipmentDataLength, nameof(equipmentData));
        ValidateBuffer(mainHandData, WeaponDataLength, nameof(mainHandData));
        ValidateBuffer(offHandData, WeaponDataLength, nameof(offHandData));
        ValidateBuffer(customizeData, CustomizeDataLength, nameof(customizeData));

        equipmentData[..EquipmentDataLength].Clear();
        mainHandData[..WeaponDataLength].Clear();
        offHandData[..WeaponDataLength].Clear();
        customizeData[..CustomizeDataLength].Clear();

        if (address == nint.Zero)
        {
            return new CharacterObjectState(
                nint.Zero,
                nint.Zero,
                GameObjectDrawCondition.ObjectZero,
                null,
                string.Empty,
                0,
                0,
                0,
                0,
                HasAppearanceData: false,
                HasHumanData: false,
                HasMainHandData: false,
                HasOffHandData: false);
        }

        var gameObject = (GameObject*)address;
        var drawObjectAddress = (nint)gameObject->DrawObject;
        var drawCondition = ReadDrawCondition(address, drawObjectAddress, inspectModelSlots);
        var objectIndex = gameObject->ObjectIndex;

        if (drawObjectAddress == nint.Zero)
        {
            return new CharacterObjectState(
                address,
                drawObjectAddress,
                drawCondition,
                objectIndex,
                string.Empty,
                0,
                0,
                0,
                0,
                HasAppearanceData: false,
                HasHumanData: false,
                HasMainHandData: false,
                HasOffHandData: false);
        }

        var character = (Character*)address;
        var name = character->GameObject.NameString;
        var classJob = character->CharacterData.ClassJob;

        if (IsHumanDrawObject(drawObjectAddress))
        {
            var human = (Human*)drawObjectAddress;
            CopyBytes((byte*)&human->Head, equipmentData, EquipmentDataLength);

            ref var mainHand = ref character->DrawData.Weapon(WeaponSlot.MainHand);
            ref var offHand = ref character->DrawData.Weapon(WeaponSlot.OffHand);
            var hasMainHandData = CopyWeapon((Weapon*)mainHand.DrawObject, mainHandData);
            var hasOffHandData = CopyWeapon((Weapon*)offHand.DrawObject, offHandData);

            human->Customize.Data.CopyTo(customizeData);

            return new CharacterObjectState(
                address,
                drawObjectAddress,
                drawCondition,
                objectIndex,
                name,
                classJob,
                human->Customize.Sex,
                human->Customize.Race,
                human->Customize.Tribe,
                HasAppearanceData: true,
                HasHumanData: true,
                hasMainHandData,
                hasOffHandData);
        }

        CopyBytes((byte*)Unsafe.AsPointer(ref character->DrawData.EquipmentModelIds[0]), equipmentData, EquipmentDataLength);
        character->DrawData.CustomizeData.Data.CopyTo(customizeData);

        return new CharacterObjectState(
            address,
            drawObjectAddress,
            drawCondition,
            objectIndex,
            name,
            classJob,
            0,
            0,
            0,
            HasAppearanceData: true,
            HasHumanData: false,
            HasMainHandData: false,
            HasOffHandData: false);
    }

    public static GameObjectDrawCondition ReadDrawCondition(nint address, nint drawObjectAddress, bool inspectModelSlots)
    {
        if (address == nint.Zero)
        {
            return GameObjectDrawCondition.ObjectZero;
        }

        if (drawObjectAddress == nint.Zero)
        {
            return GameObjectDrawCondition.DrawObjectZero;
        }

        var renderFlags = ((GameObject*)address)->RenderFlags != VisibilityFlags.None;
        if (renderFlags)
        {
            return GameObjectDrawCondition.RenderFlags;
        }

        if (inspectModelSlots)
        {
            var characterBase = (CharacterBase*)drawObjectAddress;
            if (characterBase->HasModelInSlotLoaded != 0)
            {
                return GameObjectDrawCondition.ModelInSlotLoaded;
            }

            if (characterBase->HasModelFilesInSlotLoaded != 0)
            {
                return GameObjectDrawCondition.ModelFilesInSlotLoaded;
            }
        }

        return GameObjectDrawCondition.None;
    }

    private static void CopyBytes(byte* source, Span<byte> destination, int length)
    {
        for (var i = 0; i < length; i++)
        {
            destination[i] = source[i];
        }
    }

    private static bool CopyWeapon(Weapon* weapon, Span<ushort> destination)
    {
        if ((nint)weapon == nint.Zero)
        {
            return false;
        }

        destination[0] = weapon->ModelSetId;
        destination[1] = weapon->Variant;
        destination[2] = weapon->SecondaryId;
        return true;
    }

    private static bool IsHumanDrawObject(nint drawObjectAddress)
    {
        var drawObject = (DrawObject*)drawObjectAddress;
        return drawObject->Object.GetObjectType() == ObjectType.CharacterBase
               && ((CharacterBase*)drawObjectAddress)->GetModelType() == CharacterBase.ModelType.Human;
    }

    private static void ValidateBuffer<T>(Span<T> buffer, int minimumLength, string paramName)
    {
        if (buffer.Length < minimumLength)
        {
            throw new ArgumentException("The destination buffer is too small.", paramName);
        }
    }
}
