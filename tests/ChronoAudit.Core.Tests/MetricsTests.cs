using System.Diagnostics.Metrics;

namespace ChronoAudit.Core.Tests;

public class MetricsTests
{
    [Fact]
    public void MeterName_IsChronoAudit_AndAMeterWithItIsObservableByName()
    {
        Assert.Equal("ChronoAudit", ChronoAuditMetrics.MeterName);

        using var meter = new Meter(ChronoAuditMetrics.MeterName);
        var counter = meter.CreateCounter<long>("chronoaudit.test.count");
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ChronoAuditMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => total += value);
        listener.Start();

        counter.Add(3);

        Assert.Equal(3, total);
    }
}
