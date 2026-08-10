namespace DDAI.Core.MapPlans;

public static class RectangularRoomPlanValidator
{
    public static MapPlanValidationResult Validate(MapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var issues = new List<MapPlanValidationIssue>(MapPlanValidator.Validate(plan).Issues);

        if (plan.Mode != MapOperationMode.Add)
        {
            issues.Add(new MapPlanValidationIssue(
                "unsupported_mode",
                "mode",
                "Only add mode is supported for rectangular rooms."));
        }

        if (plan.BaseRevision != 0)
        {
            issues.Add(new MapPlanValidationIssue(
                "unsupported_base_revision",
                "base_revision",
                "Rectangular room creation requires base revision zero."));
        }

        if (plan.Rooms is null || plan.Rooms.Count != 1)
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_room_count",
                "rooms",
                "Exactly one room is required."));
            return new MapPlanValidationResult(issues);
        }

        var room = plan.Rooms[0];
        if (room is null)
        {
            issues.Add(new MapPlanValidationIssue(
                "room_required",
                "rooms[0]",
                "Room is required."));
            return new MapPlanValidationResult(issues);
        }

        if (!SafeIdentifier.IsSafe(room.Id))
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_room_id",
                "rooms[0].id",
                "Room ID is unsafe."));
        }

        if (room.X < 0)
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_room_x",
                "rooms[0].x",
                "Room x must be nonnegative."));
        }

        if (room.Y < 0)
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_room_y",
                "rooms[0].y",
                "Room y must be nonnegative."));
        }

        if (room.Width <= 0)
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_room_width",
                "rooms[0].width",
                "Room width must be positive."));
        }

        if (room.Height <= 0)
        {
            issues.Add(new MapPlanValidationIssue(
                "invalid_room_height",
                "rooms[0].height",
                "Room height must be positive."));
        }

        if (room.X >= 0 && room.Width > 0 && plan.Canvas is { Width: > 0 } &&
            (room.X > plan.Canvas.Width || room.Width > plan.Canvas.Width - room.X))
        {
            issues.Add(new MapPlanValidationIssue(
                "room_out_of_bounds",
                "rooms[0]",
                "Room exceeds the declared canvas width."));
        }

        if (room.Y >= 0 && room.Height > 0 && plan.Canvas is { Height: > 0 } &&
            (room.Y > plan.Canvas.Height || room.Height > plan.Canvas.Height - room.Y))
        {
            issues.Add(new MapPlanValidationIssue(
                "room_out_of_bounds",
                "rooms[0]",
                "Room exceeds the declared canvas height."));
        }

        return new MapPlanValidationResult(issues);
    }
}
