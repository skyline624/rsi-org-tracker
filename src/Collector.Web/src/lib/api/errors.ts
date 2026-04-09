/**
 * Parsing des réponses d'erreur de l'API (RFC 7807 ProblemDetails).
 */

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  correlationId?: string;
  exceptionType?: string;
  [k: string]: unknown;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;
  readonly correlationId?: string;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.title ?? problem.detail ?? `HTTP ${status}`);
    this.status = status;
    this.problem = problem;
    this.correlationId = problem.correlationId;
    this.name = "ApiError";
  }

  static async fromResponse(res: Response): Promise<ApiError> {
    let body: ProblemDetails = {};
    try {
      const text = await res.text();
      if (text) {
        body = JSON.parse(text) as ProblemDetails;
      }
    } catch {
      // body absent ou non-JSON — on garde le problem vide
    }
    return new ApiError(res.status, {
      title: body.title ?? res.statusText,
      status: res.status,
      ...body,
    });
  }
}
