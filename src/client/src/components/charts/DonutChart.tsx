import {
  ResponsiveContainer,
  PieChart,
  Pie,
  Cell,
  Tooltip,
  Legend,
} from "recharts";
import { CHART_COLORS } from "./chart-colors";

interface DonutChartItem {
  id?: string;
  name: string;
  value: number;
}

interface DonutChartProps {
  data: DonutChartItem[];
  height?: number;
  centerLabel?: string;
  formatValue?: (value: number) => string;
  /** ARIA label used as the chart's accessible name when no labelledby id is available. */
  "aria-label"?: string;
  /** ID of an element that labels this chart (e.g. ChartCard's title id). */
  "aria-labelledby"?: string;
}

export function DonutChart({
  data,
  height = 300,
  centerLabel,
  formatValue = (v) => v.toLocaleString(),
  "aria-label": ariaLabel,
  "aria-labelledby": ariaLabelledBy,
}: DonutChartProps) {
  return (
    <div>
      {/* Visually-hidden data table — provides a text alternative (WCAG 1.1.1)
          and resolves the color-only slice identification concern (WCAG 1.4.1) */}
      <table className="sr-only">
        <caption>{ariaLabel ?? "Donut chart data"}</caption>
        <thead>
          <tr>
            <th scope="col">Category</th>
            <th scope="col">Value</th>
          </tr>
        </thead>
        <tbody>
          {data.map((item) => (
            <tr key={item.id ?? item.name}>
              <td>{item.name}</td>
              <td>{formatValue(item.value)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <div
        role="img"
        aria-label={ariaLabelledBy ? undefined : (ariaLabel ?? "Donut chart")}
        aria-labelledby={ariaLabelledBy}
      >
        <ResponsiveContainer width="100%" height={height}>
          <PieChart>
            <Pie
              data={data}
              cx="50%"
              cy="50%"
              innerRadius="55%"
              outerRadius="80%"
              paddingAngle={2}
              dataKey="value"
              nameKey="name"
              label={false}
            >
              {data.map((entry, index) => (
                <Cell
                  key={entry.id ?? `cell-${index}`}
                  fill={CHART_COLORS[index % CHART_COLORS.length]}
                />
              ))}
            </Pie>
            {centerLabel && (
              <text
                x="50%"
                y="50%"
                textAnchor="middle"
                dominantBaseline="middle"
                className="fill-foreground text-lg font-semibold"
              >
                {centerLabel}
              </text>
            )}
            <Tooltip
              formatter={(value) => [formatValue(Number(value))]}
              contentStyle={{
                backgroundColor: "hsl(var(--popover))",
                border: "1px solid hsl(var(--border))",
                borderRadius: "var(--radius)",
                color: "hsl(var(--popover-foreground))",
              }}
            />
            <Legend
              verticalAlign="bottom"
              iconType="circle"
              iconSize={8}
              wrapperStyle={{ fontSize: "12px" }}
            />
          </PieChart>
        </ResponsiveContainer>
      </div>
    </div>
  );
}
