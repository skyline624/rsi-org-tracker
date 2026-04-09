"use client";
import {
  Cell,
  Legend,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
} from "recharts";

interface Slice {
  archetype: string;
  count: number;
}

const COLORS = [
  "#00d9ff",
  "#ff7a00",
  "#3eff8b",
  "#ff2d5e",
  "#a66aff",
  "#00ffc8",
  "#ffde00",
  "#ff66aa",
];

export function ArchetypeDonut({ data }: { data: Slice[] }) {
  return (
    <div style={{ width: "100%", height: 260 }}>
      <ResponsiveContainer>
        <PieChart>
          <Pie
            data={data}
            dataKey="count"
            nameKey="archetype"
            cx="50%"
            cy="50%"
            innerRadius="55%"
            outerRadius="80%"
            paddingAngle={2}
            stroke="#0a0f14"
            strokeWidth={2}
          >
            {data.map((_, i) => (
              <Cell key={i} fill={COLORS[i % COLORS.length]} />
            ))}
          </Pie>
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
              color: "#5d7a8a",
            }}
          />
        </PieChart>
      </ResponsiveContainer>
    </div>
  );
}
