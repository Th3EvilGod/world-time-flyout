using WorldTimeFlyout;
using Xunit;

public class MotionTests
{
    [Fact]
    public void Curves_start_at_zero_end_at_one_and_never_go_back()
    {
        foreach (var m in new[] { Motion.Entrance, Motion.PointToPoint, Motion.Exit })
        {
            Assert.Equal(0, m.Ease(0), 3);
            Assert.Equal(1, m.Ease(1), 3);
            double last = 0;
            for (int i = 1; i <= 100; i++)
            {
                double v = m.Ease(i / 100.0);
                Assert.True(v >= last - 1e-6, $"{m.Name} went back at {i}%");
                last = v;
            }
        }
    }

    [Fact]
    public void Entrance_decelerates_and_exit_accelerates()
    {
        // cubic-bezier(0,0,0,1) at half time: x = s^3 = 0.5, y = 3s^2 - 2s^3 = 0.89
        Assert.Equal(0.89, Motion.Entrance.Ease(0.5), 2);
        // cubic-bezier(1,0,1,1) at half time is only about 11% of the way
        Assert.Equal(0.11, Motion.Exit.Ease(0.5), 2);
        Assert.Equal(333, Motion.Entrance.Milliseconds);
        Assert.Equal(167, Motion.Exit.Milliseconds);
    }
}
