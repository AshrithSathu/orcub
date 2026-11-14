import { bucket, defineRailway, postgres, project, service, volume } from "railway/iac";

export default defineRailway(() => {
  const Postgres = postgres("Postgres", { region: "us-west2" });
  Postgres.networking = { privateNetworkEndpoint: "postgres" };
  const postgresVolume = volume("postgres-volume", { alerts: { usage: { "100": {}, "80": {}, "95": {} } }, allowOnlineResize: true, region: "us-west2", sizeMB: 50000 });
  const PostgresPITR = bucket("Postgres-PITR", { region: "sjc" });
  const api = service("api", {
    build: { buildEnvironment: "V3", builder: "DOCKERFILE", dockerfilePath: "Dockerfile" },
    healthcheck: "/readyz",
    healthcheckTimeout: 120,
    replicas: { "us-west2": 1 },
    deploy: { drainingSeconds: 40, overlapSeconds: 15, restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 10, sleepApplication: false },
    domains: ["orcub.sathuashrith.com"],
  });

  return project("orcub", {
    variables: { managed: false },
    resources: [Postgres, api, postgresVolume, PostgresPITR],
  });
});
