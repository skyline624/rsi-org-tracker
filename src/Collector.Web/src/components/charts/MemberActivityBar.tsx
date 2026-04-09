"use client";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

interface Point {
  date: string;
  joins: number;
  leaves: number;
}

export function MemberActivityBar({ data }: { data: Point[] }) {
  return (
    <div style={{ width: "100%", height: 260 }}>
      <ResponsiveContainer>
        <BarChart data={data} margin={{ top: 16, right: 16, bottom: 0, left: 0 }}>
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
          />
          <Legend
            wrapperStyle={{
              fontFamily: "monospace",
              fontSize: "0.65rem",
              textTransform: "uppercase",
              letterSpacing: "0.1em",
            }}
          />
          <Bar dataKey="joins" fill="#3eff8b" stackId="a" />
          <Bar dataKey="leaves" fill="#ff2d5e" stackId="a" />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}
