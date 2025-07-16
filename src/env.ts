import { createEnv } from "@t3-oss/env-core";
import { config } from "dotenv";
import { z } from "zod";

config();

export const env = createEnv({
  clientPrefix: "PUBLIC_",
  client: {},
  server: {
    API_KEY_EMBY: z.string(),
    EMBY_HOSTNAME: z.string(),
  },
  runtimeEnv: process.env,
});
