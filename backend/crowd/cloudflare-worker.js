import { WorkerEntrypoint } from "cloudflare:workers";
import worker, { usageReport } from "./worker.js";

export default worker;

export class UsageReport extends WorkerEntrypoint {
  async readReport() {
    const report = await usageReport(this.env);
    if (Array.isArray(report.devices)) return report;

    // Внутренний отчет включает таблицу и на сборках с короткой публичной сводкой.
    const list = await this.env.DB.prepare(`
      SELECT mask, first_day, last_day, days, version, source, replaced, token_changes, moved, total_sec, last_sec, runs
      FROM devices
      ORDER BY last_day DESC, total_sec DESC
      LIMIT 200
    `).all();
    return { ...report, devices: list.results };
  }
}
