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
    PROXY_STRING: z.string().optional(),
    COOKIES_FILE: z.string().optional(),
  },
  runtimeEnv: process.env,
});
