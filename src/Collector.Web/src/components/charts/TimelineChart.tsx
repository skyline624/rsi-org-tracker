"use client";
import {
  ResponsiveContainer,
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
} from "recharts";

interface Point {
  date: string;
  value: number;
}

interface TimelineChartProps {
  data: Point[];
  height?: number;
  color?: string;
  label?: string;
}

export function TimelineChart({
  data,
  height = 220,
  color = "#00d9ff",
  label = "value",
}: TimelineChartProps) {
  return (
    <div style={{ width: "100%", height }}>
      <ResponsiveContainer>
        <LineChart data={data} margin={{ top: 16, right: 16, bottom: 0, left: 0 }}>
          <defs>
            <linearGradient id="gradCyan" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={color} stopOpacity={0.5} />
              <stop offset="100%" stopColor={color} stopOpacity={0} />
            </linearGradient>
          </defs>
          <CartesianGrid stroke="rgba(0,217,255,0.1)" strokeDasharray="2 4" />
          <XAxis
            dataKey="date"
            stroke="#5d7a8a"
            tick={{ fontFamily: "monospace", fontSize: 10 }}
          />
          <YAxis
            stroke="#5d7a8a"
            tick={{ fontFamily: "monospace", fontSize: 10 }}
            width={40}
          />
          <Tooltip
            contentStyle={{
              background: "#0f1821",
              border: "1px solid #00d9ff",
              fontFamily: "monospace",
              fontSize: "0.75rem",
              color: "#c9e6f2",
              textTransform: "uppercase",
            }}
            labelStyle={{ color: "#5d7a8a" }}
            formatter={(v: number) => [v.toLocaleString(), label]}
          />
          <Line
            type="monotone"
            dataKey="value"
            stroke={color}
            strokeWidth={2}
            dot={{ r: 2, fill: color, stroke: "none" }}
            activeDot={{ r: 4, fill: color, stroke: "#fff", strokeWidth: 1 }}
            animationDuration={400}
          />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
