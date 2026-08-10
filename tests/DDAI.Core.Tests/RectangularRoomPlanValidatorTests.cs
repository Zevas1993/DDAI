using DDAI.Core.MapPlans;

namespace DDAI.Core.Tests;

public sealed class RectangularRoomPlanValidatorTests
{
    [Fact]
    public void Validate_AcceptsOneInBoundsAddRoom()
    {
        var result = RectangularRoomPlanValidator.Validate(ValidPlan());

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(MapOperationMode.Replace, 0, "unsupported_mode", "mode")]
    [InlineData(MapOperationMode.Patch, 0, "unsupported_mode", "mode")]
    [InlineData(MapOperationMode.Add, 1, "unsupported_base_revision", "base_revision")]
    public void Validate_RejectsUnsupportedCommandSemantics(
        MapOperationMode mode,
        long revision,
        string code,
        string path)
    {
        var result = RectangularRoomPlanValidator.Validate(
            ValidPlan() with { Mode = mode, BaseRevision = revision });

        var issue = Assert.Single(result.Issues);
        Assert.Equal((code, path), (issue.Code, issue.Path));
    }

    [Fact]
    public void Validate_RejectsNullRoomsWithoutDereferencing()
    {
        var result = RectangularRoomPlanValidator.Validate(ValidPlan() with { Rooms = null! });

        Assert.Equal(
        [
            ("invalid_rooms", "rooms"),
            ("invalid_room_count", "rooms"),
        ],
            result.Issues.Select(issue => (issue.Code, issue.Path)));
    }

    [Fact]
    public void Validate_RejectsNoRooms()
    {
        var issue = Assert.Single(
            RectangularRoomPlanValidator.Validate(ValidPlan() with { Rooms = [] }).Issues);

        Assert.Equal(("invalid_room_count", "rooms"), (issue.Code, issue.Path));
    }

    [Fact]
    public void Validate_RejectsMoreThanOneRoom()
    {
        var plan = ValidPlan() with
        {
            Rooms =
            [
                new MapRoom("one", 1, 1, 2, 2),
                new MapRoom("two", 4, 4, 2, 2),
            ],
        };

        var issue = Assert.Single(RectangularRoomPlanValidator.Validate(plan).Issues);

        Assert.Equal(("invalid_room_count", "rooms"), (issue.Code, issue.Path));
    }

    [Fact]
    public void Validate_RejectsNullRoomWithoutDereferencing()
    {
        var issue = Assert.Single(
            RectangularRoomPlanValidator.Validate(
                ValidPlan() with { Rooms = [null!] }).Issues);

        Assert.Equal(("room_required", "rooms[0]"), (issue.Code, issue.Path));
    }

    [Theory]
    [InlineData("", "invalid_room_id", "rooms[0].id")]
    [InlineData(".", "invalid_room_id", "rooms[0].id")]
    [InlineData("..", "invalid_room_id", "rooms[0].id")]
    [InlineData("unsafe/id", "invalid_room_id", "rooms[0].id")]
    [InlineData("unsafe\\id", "invalid_room_id", "rooms[0].id")]
    public void Validate_RejectsUnsafeRoomId(string id, string code, string path)
    {
        var issue = Assert.Single(
            RectangularRoomPlanValidator.Validate(
                WithRoom(ValidPlan(), new MapRoom(id, 8, 7, 10, 8))).Issues);

        Assert.Equal((code, path), (issue.Code, issue.Path));
    }

    [Theory]
    [InlineData(-1, 7, 10, 8, "invalid_room_x", "rooms[0].x")]
    [InlineData(8, -1, 10, 8, "invalid_room_y", "rooms[0].y")]
    [InlineData(8, 7, 0, 8, "invalid_room_width", "rooms[0].width")]
    [InlineData(8, 7, 10, 0, "invalid_room_height", "rooms[0].height")]
    public void Validate_RejectsInvalidScalar(
        int x,
        int y,
        int width,
        int height,
        string code,
        string path)
    {
        var issue = Assert.Single(
            RectangularRoomPlanValidator.Validate(
                WithRoom(ValidPlan(), new MapRoom("room", x, y, width, height))).Issues);

        Assert.Equal((code, path), (issue.Code, issue.Path));
    }

    [Theory]
    [InlineData(31, 7, 10, 8)]
    [InlineData(8, 23, 10, 8)]
    [InlineData(40, 7, 1, 8)]
    [InlineData(8, 30, 10, 1)]
    [InlineData(int.MaxValue, 7, int.MaxValue, 8)]
    [InlineData(8, int.MaxValue, 10, int.MaxValue)]
    public void Validate_RejectsOutOfBoundsWithoutOverflow(int x, int y, int width, int height)
    {
        var issues = RectangularRoomPlanValidator.Validate(
            WithRoom(ValidPlan(), new MapRoom("room", x, y, width, height))).Issues;

        var issue = Assert.Single(issues);
        Assert.Equal(("room_out_of_bounds", "rooms[0]"), (issue.Code, issue.Path));
    }

    [Fact]
    public void Validate_ReturnsEveryIssueInStableFieldOrder()
    {
        var plan = new MapPlan
        {
            SchemaVersion = "2.0",
            RequestId = "../unsafe",
            BaseRevision = 2,
            Mode = MapOperationMode.Patch,
            Canvas = new MapCanvas(40, 30),
            Rooms = [new MapRoom("../room", -1, -2, 0, 0)],
        };

        var result = RectangularRoomPlanValidator.Validate(plan);

        Assert.Equal(
        [
            "unsupported_schema_version",
            "invalid_request_id",
            "unsupported_mode",
            "unsupported_base_revision",
            "invalid_room_id",
            "invalid_room_x",
            "invalid_room_y",
            "invalid_room_width",
            "invalid_room_height",
        ],
            result.Issues.Select(issue => issue.Code));
    }

    private static MapPlan WithRoom(MapPlan plan, MapRoom room) =>
        plan with { Rooms = [room] };

    private static MapPlan ValidPlan() => new()
    {
        SchemaVersion = MapPlan.CurrentSchemaVersion,
        RequestId = "room-job-001",
        BaseRevision = 0,
        Mode = MapOperationMode.Add,
        Canvas = new MapCanvas(40, 30),
        Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
    };
}
