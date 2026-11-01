import { useId, useMemo } from "react";
import {
  ResponsiveContainer,
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
} from "recharts";
import { CHART_COLORS } from "./chart-colors";

interface DataPoint {
  period: string;
  amount: number;
}

interface AreaTimeChartProps {
  data: DataPoint[];
  trendlineData?: DataPoint[];
  height?: number;
  color?: string;
  trendlineColor?: string;
  formatValue?: (value: number) => string;
  /** ARIA label used as the chart's accessible name when no labelledby id is available. */
  "aria-label"?: string;
  /** ID of an element that labels this chart (e.g. ChartCard's title id). */
  "aria-labelledby"?: string;
}

export function AreaTimeChart({
  data,
  trendlineData,
  height = 300,
  color = CHART_COLORS[0],
  trendlineColor = CHART_COLORS[1],
  formatValue = (v) => v.toLocaleString(),
  "aria-label": ariaLabel,
  "aria-labelledby": ariaLabelledBy,
}: AreaTimeChartProps) {
  const gradientId = useId();

  const mergedData = useMemo(() => {
    if (!trendlineData || trendlineData.length === 0) return data;
    return data.map((point, i) => ({
      ...point,
      trendline: trendlineData[i]?.amount,
    }));
  }, [data, trendlineData]);

  const hasTrendline = trendlineData && trendlineData.length > 0;

  return (
    <div>
      {/* Visually-hidden data table — provides a text alternative (WCAG 1.1.1)
          and resolves the color-only series identification concern (WCAG 1.4.1) */}
      <table className="sr-only">
        <caption>{ariaLabel ?? "Area time chart data"}</caption>
        <thead>
          <tr>
            <th scope="col">Period</th>
            <th scope="col">Amount</th>
            {hasTrendline && <th scope="col">Rolling Average</th>}
          </tr>
        </thead>
        <tbody>
          {mergedData.map((row, index) => (
            // Exact granularity repeats a period for several purchases on one day.
            <tr key={`${row.period}-${index}`}>
              <td>{row.period}</td>
              <td>{formatValue(row.amount)}</td>
              {hasTrendline && (
                <td>
                  {(row as unknown as { trendline?: number }).trendline !== undefined
                    ? formatValue((row as unknown as { trendline: number }).trendline)
                    : ""}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>

      <div
        role="img"
        aria-label={ariaLabelledBy ? undefined : (ariaLabel ?? "Area time chart")}
        aria-labelledby={ariaLabelledBy}
      >
        <ResponsiveContainer width="100%" height={height}>
          <AreaChart
            data={mergedData}
            margin={{ top: 5, right: 20, bottom: 5, left: 0 }}
          >
            <defs>
              <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
                <stop offset="5%" stopColor={color} stopOpacity={0.3} />
                <stop offset="95%" stopColor={color} stopOpacity={0} />
              </linearGradient>
            </defs>
            <CartesianGrid strokeDasharray="3 3" className="stroke-border" />
            <XAxis
              dataKey="period"
              tick={{ fontSize: 12 }}
              className="fill-muted-foreground"
            />
            <YAxis
              tick={{ fontSize: 12 }}
              className="fill-muted-foreground"
              tickFormatter={formatValue}
            />
            <Tooltip
              formatter={(value, name) => [
                formatValue(Number(value)),
                name === "trendline" ? "Rolling Avg" : "Amount",
              ]}
              contentStyle={{
                backgroundColor: "hsl(var(--popover))",
                border: "1px solid hsl(var(--border))",
                borderRadius: "var(--radius)",
                color: "hsl(var(--popover-foreground))",
              }}
            />
            <Area
              type="monotone"
              dataKey="amount"
              stroke={color}
              fill={`url(#${gradientId})`}
              strokeWidth={2}
            />
            {hasTrendline && (
              <Area
                type="monotone"
                dataKey="trendline"
                stroke={trendlineColor}
                fill="none"
                strokeWidth={2}
                strokeDasharray="5 5"
                strokeOpacity={0.7}
                dot={false}
                name="trendline"
              />
            )}
          </AreaChart>
        </ResponsiveContainer>
      </div>
    </div>
  );
}
