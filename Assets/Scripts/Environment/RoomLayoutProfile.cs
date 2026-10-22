using UnityEngine;

namespace Freehand.Environment
{
    public enum RoomSizeMode
    {
        Compact,
        Standard,
        Large,
    }

    /// <summary>
    /// Every distance that shapes the practice room, in one place, so tuning
    /// the layout never means hunting for a magic number buried in a builder
    /// method. AdaptiveRoomController applies one of these to the room's
    /// existing props; it never creates a second set of geometry per profile.
    ///
    /// All distances are meters, measured from the player/tracking origin
    /// (which this project always keeps at world (0,0,0) - Tracking Origin
    /// Mode is Floor, there is no teleport/room offset system), along the
    /// room's own +Z (paint wall side) and +X (right/palette side) axes.
    /// </summary>
    [System.Serializable]
    public class RoomLayoutProfile
    {
        public string profileName = "Standard";

        [Header("Paint wall")]
        public float wallDistance = 1.9f;
        public float wallWidth = 4.6f;
        public float wallHeight = 2.8f;

        [Header("Room shell")]
        public float roomHalfWidth = 2.4f;
        public float roomHalfDepth = 2.0f;

        [Header("Color palette (right side)")]
        [Tooltip("Distance from the player to the color palette.")]
        public float paletteDistance = 0.68f;
        public float paletteAngleDegrees = 35f;

        [Header("Tool table (left side): can rest + cap rack")]
        [Tooltip("Table centre, sideways from the player (negative = left).")]
        public float toolTableSide = -0.62f;

        [Tooltip("Table centre, forward from the player.")]
        public float toolTableForward = 0.2f;

        [Tooltip("Table width (x), height (y), depth (z), meters. Waist height, narrow, long front-to-back so the can and the rack sit one behind the other within arm's reach.")]
        public Vector3 toolTableSize = new Vector3(0.3f, 0.9f, 0.5f);

        [Tooltip("Where the can rests on the table top, (x, z) from the table centre.")]
        public Vector2 canRestOnTable = new Vector2(0f, 0.12f);

        [Tooltip("Where the cap rack sits on the table top, (x, z) from the table centre.")]
        public Vector2 capRackOnTable = new Vector2(0f, -0.1f);

        [Header("Can workshop (behind the player, V0.12)")]
        [Tooltip("Workbench centre, sideways from the player.")]
        public float workshopSide = 0.1f;

        [Tooltip("Workbench centre, forward from the back wall's centre line (it stands in front of the back wall, behind the player).")]
        public float workshopFromBackWall = 0.75f;

        [Header("Comfort")]
        [Tooltip("Radius around the player's tracking origin that must stay free of fixed props - room to raise arms and turn without bumping anything.")]
        public float minimumComfortRadius = 0.4f;

        [Tooltip("Distances beyond this are only used by Large - kept as a sanity ceiling so a future profile cannot accidentally push the wall out of the spray's own maxDistance range.")]
        public float maximumComfortDistance = 2.8f;

        // V0.8: widened from the V0.5 values after physical Quest 3 testing
        // reported the room feeling cramped and the can bumping the palette
        // while spraying forward. Palette pushed further out AND more to the
        // side (higher angle) so it sits outside the cone the player
        // naturally sweeps through when aiming at the main wall.
        //
        // V0.8.x: the can pedestal used to stand in front of the player
        // (0.4m ahead, right in the spraying cone). It is now a tool table
        // on the left, just outside the comfort radius, carrying both the
        // can and the cap rack - the whole space between player and wall is
        // free, and everything is still reachable by leaning, not walking.
        public static RoomLayoutProfile Compact() => new RoomLayoutProfile
        {
            profileName = "Compact",
            wallDistance = 1.5f,
            wallWidth = 3.6f,
            wallHeight = 2.5f,
            roomHalfWidth = 1.8f,
            roomHalfDepth = 1.6f,
            paletteDistance = 0.75f,
            paletteAngleDegrees = 42f,
            toolTableSide = -0.62f,
            toolTableForward = 0.2f,
            minimumComfortRadius = 0.42f,
            maximumComfortDistance = 1.7f,
        };

        public static RoomLayoutProfile Standard() => new RoomLayoutProfile
        {
            profileName = "Standard",
            wallDistance = 1.9f,
            wallWidth = 4.6f,
            wallHeight = 2.8f,
            roomHalfWidth = 2.4f,
            roomHalfDepth = 2.0f,
            paletteDistance = 0.68f,
            paletteAngleDegrees = 35f,
            toolTableSide = -0.66f,
            toolTableForward = 0.25f,
            minimumComfortRadius = 0.4f,
            maximumComfortDistance = 2.1f,
        };

        public static RoomLayoutProfile Large() => new RoomLayoutProfile
        {
            profileName = "Large",
            wallDistance = 2.7f,
            wallWidth = 6.6f,
            wallHeight = 3.2f,
            roomHalfWidth = 3.3f,
            roomHalfDepth = 2.8f,
            paletteDistance = 0.8f,
            paletteAngleDegrees = 30f,
            toolTableSide = -0.7f,
            toolTableForward = 0.3f,
            minimumComfortRadius = 0.45f,
            maximumComfortDistance = 2.8f,
        };

        // Placement shared by V04SceneBuilder (edit time) and
        // AdaptiveRoomController (runtime), so both always put the table, can
        // and rack in exactly the same spot. `origin` is the player's floor
        // position; the room is axis-aligned (+Z toward the paint wall).
        public Vector3 ToolTableCenter(Vector3 origin) => origin + new Vector3(toolTableSide, toolTableSize.y * 0.5f, toolTableForward);

        public Vector3 ToolTableTop(Vector3 origin) => origin + new Vector3(toolTableSide, toolTableSize.y, toolTableForward);

        // 2mm above the top, so the can's collider starts clear of the table
        // instead of overlapping it on the first physics step.
        public Vector3 CanRestPosition(Vector3 origin) => ToolTableTop(origin) + new Vector3(canRestOnTable.x, 0.002f, canRestOnTable.y);

        public Vector3 CapRackPosition(Vector3 origin) => ToolTableTop(origin) + new Vector3(capRackOnTable.x, 0f, capRackOnTable.y);

        /// <summary>Rack +Z faces the player, so parked caps show their orifice side.</summary>
        public Quaternion CapRackRotation(Vector3 origin)
        {
            var toPlayer = origin - CapRackPosition(origin);
            toPlayer.y = 0f;
            return toPlayer.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(toPlayer.normalized, Vector3.up) : Quaternion.identity;
        }

        /// <summary>The workbench's position (floor level) - it faces the player (its front toward +Z).</summary>
        public Vector3 WorkshopPosition(Vector3 origin) => origin + new Vector3(workshopSide, 0f, -roomHalfDepth + workshopFromBackWall);

        /// <summary>The workbench's rotation: its front (local -Z) toward the player, who turns round to it.</summary>
        public static Quaternion WorkshopRotation => Quaternion.Euler(0f, 180f, 0f);

        // V0.13: the mode panel (VR / MR switch, room scan, status) stands beside the tool table,
        // on the player's side of it, so it is reached with the same step as the table.
        public Vector3 MRPanelPosition(Vector3 origin)
        {
            var table = ToolTableCenter(origin);
            return new Vector3(table.x - toolTableSize.x * 0.5f - 0.2f, origin.y, table.z - 0.12f);
        }

        /// <summary>The panel's front (local -Z) toward the player's spot.</summary>
        public Quaternion MRPanelRotation(Vector3 origin)
        {
            var away = MRPanelPosition(origin) - origin;
            away.y = 0f;
            return away.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(away.normalized, Vector3.up) : Quaternion.identity;
        }

        public static RoomLayoutProfile ForMode(RoomSizeMode mode) => mode switch
        {
            RoomSizeMode.Compact => Compact(),
            RoomSizeMode.Standard => Standard(),
            RoomSizeMode.Large => Large(),
            _ => Compact(),
        };
    }
}
