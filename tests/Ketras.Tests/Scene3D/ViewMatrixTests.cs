using System.Numerics;
using Ketras.Controls.Scene3D.Rendering;

namespace Ketras.Tests.Scene3D;

public sealed class ViewMatrixTests
{
    [Fact]
    public void Looking_north_east_is_on_the_right_and_a_right_bank_lowers_the_right_side()
    {
        // From 100 m south of the origin, looking north: +X (east) must land on the screen's right (view +X).
        var view = SceneRenderer.ViewMatrix(new Vector3(0, 0, -100), Vector3.Zero);
        Assert.True(Vector3.Transform(new Vector3(10, 0, 0), view).X > 0);
        Assert.True(Vector3.Transform(new Vector3(0, 10, 0), view).Y > 0);
        Assert.True(Vector3.Transform(Vector3.Zero, view).Z < 0);

        // The right wing tip of an aircraft heading north and banked 30° right sits right of and below the centre.
        var bank = Matrix4x4.CreateRotationZ(-MathF.PI / 6);
        var tip = Vector3.Transform(Vector3.Transform(new Vector3(5, 0, 0), bank), view);
        Assert.True(tip.X > 0 && tip.Y < Vector3.Transform(Vector3.Zero, view).Y);
    }
}
