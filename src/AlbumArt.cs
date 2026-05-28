using MelonLoader;
using System;
using System.IO;
using System.IO.Compression;
using UnhollowerBaseLib;
using UnityEngine;
using UnityEngine.UI;

namespace ExScoringMod
{
    internal static class AlbumArt
    {
        // ── Layout tweaks ────────────────────────────────────────────────
        // Position of the album art canvas in the launch panel's local space.
        private static readonly Vector3 ArtPosition = new Vector3(3.5f, 3.25f, -0.05f);

        // Uniform scale of the canvas object. Keep X == Y for a square image.
        private static readonly Vector3 ArtScale = new Vector3(0.012f, 0.012f, 0.012f);

        // Size of the RawImage rect in canvas units (before scale is applied).
        private static readonly Vector2 ArtRectSize = new Vector2(512f, 512f);
        // ────────────────────────────────────────────────────────────────

        private static GameObject canvasObject;
        private static RawImage rawImage;
        private static Texture2D currentTexture;
        private static Texture2D defaultTexture;

        // Zlib-compressed raw RGBA pixel data (128x128, bottom-up), base64 encoded
        private static readonly string DefaultArtBase64 =
            "eNpEvAVUFYn7/88nvp/Yrs+G6666dqNigoiEnahISKOUIggCkpIKBiKiKEi3lFIqJaUiAoqACogiYnfX+vo93D3n/z97njN35g5z5877ed4xd1z/Bth8Clwr" +
            "wV9eB12A4IuwXWqb1I5WCGgEv3Ow//pf677nnrPz8id2tUFInexbA/YZz7GIv8falIcYHOpidVQ7FsndGERfxrnwCd4lb3HMfohd8iMc0l6yJqIXZatipq8v" +
            "YYp1AZPX5Stqul0RsxxLmWFXwCjDRMabpjHWJIGJa9OY6ZjLbJd8ZjplM9UhjemOGUxcl4i2ewFq67OY61bMwIVhjFwZhZZtHis9KtH3rmaRUyE6djmYBFZj" +
            "EXoW67DzmGyvwjq8mg37SnA+VELCmcfYhZWwwDEB56gG3GIuEZb/CLfoVmx2nsZhbyOW22pxDG/Bae9V3A7cwMSjmmXWmWisjGDKvGD6jbFjwMQNDJzkwC/j" +
            "rBmgsoHfJ9vz/Sgzhsx05ruxFvxP2Zo/NFz5XLYNUndlwFQ3flNxQ3n+TkbPC2X8knAm6UXxx9wdTNCPZsjiMEYsj0DFKBYVg8NMMYhG0zIVHYtkZhrG8M/B" +
            "Vnw20o4fJrnw6wxP+qt6KWqQVgCjFu1kwoq9/KEdoKhRi3bw2ywfec+fX1Q9GDo3mGWRbYpak3CTtZmPsEx/gEXafWyOPME+9ynrMu+wpfwdLsWPMU1sY2vN" +
            "M1xLbmGadA6ThHqMY5pYGVHHZNccJrlkoRVcJlXCrIACpvrI+rYCdEKltheiGVjA7K1FzAuuZmXYRZaHNjHfp4rlAWcx3HkR47AWTMPbsI2+gVNiLxtiu3FO" +
            "vs2m5B7cMu7iV/iMkJLXhJa/YU/NnxyohwTpxzjp0/QroO1SgJXgM9UilX6a21HWO8yCjUWscC9nlYeUZynL3YrQ31qGUcApTLeVsD68BCOfVGJPPaL4Kqz2" +
            "zMUiqITVHsdYuikLA68C1oVUssa3mM1RzVj4V7DIPouBs/wZM38HIzS2MnP5bnQM9iuwH6SykYk6vsxYuh31FTuZviQITYMwJi3Yyvh5PriEnWbznjOMni/7" +
            "rNzFIqtkRs0O5Lcp7qgbxKCyYh8TdCMZr7uPUcvCGbcqikmG0YxbcYDpa2LRWZfBfJsMZqw+yBcj7flm7Eb6Td/C76re/Cy9NFDdhzELdyhqkJzbqPkhjFqw" +
            "jd/UvKQH/BiqHciweUGMXhAqfbCVn3XD6Sf9+8eaaIabJzDSLIEJcnyNzcXoeJYwz+ckZgcuYB/fRuCJBzimtuBT2EVoxR22llzDs+wyIQ232Nn8gH3tLzjY" +
            "/Y5Dve9JeQopzyH7HaS9+GtZBOS9lu33IfGWYHdDXgunxF+GQ4LhXuGSnZVv5NivCDz+DLcjt3BK7sA5VT4np5dA6cFtJx6zNf8u3nm9+By9rXh/R+lr4ZBE" +
            "3NJ6SWyCPSUwx6GAYUv2M3ShXM/VsWjZ5LDQsYA1/tVYh57DPqwBq22VrA0+gV2o9MGOSnwOt7B4fQoGbvlYB1cQlnNHlmWsk33M/QoYt8SXQbNd+F1mebp+" +
            "GFNX7mSOaRQLzaOZtmQ7Xw8xZpiqCzqG4WivDkNZyxPVJcFMmOMt2yLQNtjLxHn+in3VVu1GdUUYM1cJdyzZzU/Kjsy1TEZdsJ64NBwN03i01qagZZXGfPsj" +
            "CtxVjQ+z1CGXaXr7+b/BlvSb4sbPKpvpN82VgWreDNXyY9ScIEbODWKYtj+DZ/sqliq6u5llLPjO9WfcolDGLNomHOSjWLdLu4ZRVCMLt1cwwy2HMdZxDDM/" +
            "yCCDffywKIjfV+3gqzlb+GyWE6t2nEAkgkrBsPwlnPoIFbJ+Uir/AxR8glxZZksVyrajUpnyOunlG9LffCT5xXviH74n5s57dlx6gEdFF0E1j/ApuYtnocz4" +
            "UZn3I51SXaxPvoxZpPDL3rPo76hiZVAZq4LLMQitwXT3adbuO4/9oWY80nvQcMpiU3wXW1Lv4BR7E8+0x3gkPxRuv84EgwQGz9/DmOUHmCWcaeZfhWVQFU7h" +
            "53Hb34TjrlrFTDpsr8Zldx02Mt8LrBIw2pyH6ZZjGG7OZNn6WJQXCbdOs2aKrh/TVwWhsmwrqqtDmG24hzlrIpm8MJBflNcpsNZcvZuFJvtZZHoA3bWHmWcU" +
            "iY7+XuavOcAcua5zjaJYYHKYmTJ72msOsWpjFgOmuaOyeBerHPJYsC4NHbMkBcfPkpnvq/nr0jGWeZy4bA+fDbPmt+ke/DjRie/GbaDfVGeGC6crLw4RDQnk" +
            "j1meDJntzdgFgYptf8zawriF2xim48OkZaGMmLtVsW30ggB2CucFX/pTCkJlDsM7ROdlLiNkGdT4AZ/aZ7iU9GKfdxU1/1SG2Oxg7+XX5L2Cg9ffkXgPom++" +
            "JfLaK2LldfxDiJH5jpP5P/jwE4ce/UnE7ddEPfiTXV0vCGl7yq72t3jW3sU+v4NNx7pwPnoDl2M9OGRdxyK2GZMDjazZ14T+7vMY7xF9CTrDIq9yFnqWsWJr" +
            "DYZBdZiGNmKx6yLqttkYbqvHancbVnuuYicmxUZw3xh9G8tdV7Hd08FMy0yGL9jJEJkNVaN9TNXbiYbJHuZZ7cfGrxj7wFI2hZ7Ga98FfCKamWccg5lrHra+" +
            "x9HbkMC4Oa6M0nRkhu5WRs9xZtoKf+ZZRKCuvwP11buYbxHFzJWh/DF9g8y/Iz+Lzv8yxorBUxwYqGzPtHkBTNbxQ3XxDhabxrFgTQxLzZMw3ChYW8Rj4JzD" +
            "lGW7GD7bR/DPQdcuk8Uy933ewmTzcRaYJ7Pa8RhTl+3l21Hr+WWyaP1UV5n/zfw0wYHROlsVfz9OPmeYhidj5vgxafF2qWCmr9jN+Pn+jND0Zri2Fz+IL+k/" +
            "3Un22yL65YdP82s8m17I8g1bW97he/Gv8r/4Hu/zr9jW+pGoHvF48v6+LnAuvUY/Kz9sjjWR9boP+78qvP0DEV2fCOv4k6CWVwS0vca35Rlbmu6zuf422669" +
            "JfDyKzzOPcK19iFrBfd1eddxKrzNhtweNuTcwj7rFtZJ17GSWbaI7sI44irLAhtY7HuW+Vtqme9Ww4LN1cx1rGC2TTGqFkdZ7FLNCo86TLZdxji4jcWup9H1" +
            "Po9VmHhQvwY2RF5jTcBptG2kd7W9+XW6PeMXuLHWJ409Gc2Ep7cTGH0RK4989B3SZS4PorZsB7NEm9WWBzNSeG/SfA9mrghEfWUQOka7BOvtCk1fZBEtOB1i" +
            "5YYk1FeFMFLDBY1VoaL9wUyRaztB24cZi7cxfMZmhglHjxIN/k15E39M3iLasJ3+k1yZIhowRbRAyyxGgeFC8QPa0h/L1qazwuYIiy1SMHUpZrRmMD+MdmCw" +
            "mo9i7vt4v2/m++Z6gujJROH0PvzHCqf3YT9Nd4dgv5Pfpjpi5VNMrmirssx73Mln+Im3VV4oPaHlgb9g7tX4QjB/Q0DrB/wu/IV9Hy9ECN7ep58ReukjW88+" +
            "xa2ilyjZFn75nfi7VJbtKybiqviwToQnnrCp7A6eZ1+y9aLsL5zi3vCSjTUPWFdyA4+ml7idf4FTzVNcT79hY+lzvM6AV9WfbDj6GIvUXvGU4itje1gjDbVq" +
            "Tyfz/S+y2L+ZuR4NaDhUM23tSaaYFTDNrBA1i2LULUvQsalCzfQEaualzN14RlFL3RqZZVvCUvczTDGR62mXwQrxpxt3VbAj9SKZVbfJqr5J0sl2Eo53k1B8" +
            "m9za1+xJvcJwmd/Rs1yYoOPBH1NsmbFE+H7xVibN9WK23i409cKFvw+Jvh8UjFKlB2Iw2pSOlviB8dqeCu2fKddeQzDV1o9g9qo98jcR8v5B0YVD0jeHmW0Q" +
            "J7ov/K9/mBHixfrNdKO/2hZ+VNnEiNkBDJrmye8T3fh+mL14Sje+HWrHwKne8l4gQ8R3DtH0Z4C6r8Lbqa+JEk3YyfhF2xW9NGN1uPiS3YpSFc2Zrife1OIQ" +
            "wUkdzLWKwdA9h5zzsMIhhZFyvv5tH9jS8ByfC28JEv4PvPQJn4a3bG14h1/jewJFAwLOy2vBdXvTO3yqH+FVeZ+wlg9YyfwsCDuBV/UTyYufhNNf4lrzCseq" +
            "52ysfo595VPWV0uGKLuPZzO41UuOOP0WL/l8W/F3tuLnrY48wCi+Bz3x/LrSSIvDrrJYmm/pzk6WbG9nrs9F5nldQGfzOdTsKpgk2E8wymWSUR5TjPJRWXWM" +
            "2RYVaFhWMtOijPkOZ1CzOoG69Qlm2xdjEdLIvuOvqRIOE8vJ9bfQLXVVdOrKI2gWrco78wKvvZUsMAtn2IwNTFvkK33gwBzDXYzXcmVjQBEhh5sZqe6GoUM2" +
            "6ssjWWWdyxLzVHTX9fF4qgLvqeKXlq2NFd4IZcqCIKbK7Ksu3yW6sYeZkukmL4sQPA6hZhjPlJUxaAivzzSPY6lzNhNXhvO7uicjtIMUM/7HdG/57O2MUhc/" +
            "pxEs879d5nW76HYAg3WCZP99TFolvmPVXtTEV6ibRIu27RefdxAdK+kxs4Oic2Goid7NMNjDLJNIxXLsIuGIhcJNS4OYLtq1+fwb3Brf4dGHn2C0peEDm8+8" +
            "Vsyo+1nhhnMf8K4XHyBLz7Ov8ah5ITg/x6/+rQLzLaceYpjQgH3hLdyqX2Jf8pj1Zc+xLnuKzann2EkvGOffxFxmzEeOv/nMOzaWy/ErP2Am2XJt1kPWJPWy" +
            "NLJd0QNLIzpZsFMyaVgns7zPoePTgI40TN99gkWyPsYwjQnG6cIDWSivTkVz7QnR9cNoWpUxxTCPqdIbk9cckeycwkT9OJY5Fyp8voXo/HyrCJau24Pp5kPY" +
            "eh3Gf/8JgmJOo6EfzDB1B0YI149Qd5T53abogXlrwpi1QvCYakdB3SeFf19tn8l8kwQWm2eywjoHw/VZrNmYIV7vEGNFe/v4QaEh4rlnSE1aHCrzJx7OJJap" +
            "BgcVHnSacQLj9aKZLllrsvEhdBxS0bJLYZB4hJHzBGedbQxWD0B53m7xHaGMnRemqOE6Oxi9KJzJRjGomicyVXzKNPGPc2xSUDeLY5Z5LIscjqBqIpnEPlXR" +
            "D/Ntk5lrHS/LRDQtD7LYIRktC+Ei8yhFT8ho4X7+A651H3Cv/1O0QLhctgWIH9wu2dpLdGNr332hi3+VyDG+Zz/gVStzXPNadOEtruVPMcvqUnC6TfFT1uTc" +
            "xa7iLbrp3ViceILz2U84VL7CXa7h+hPPFNg7Fr7+615Dyj1sc16wWjjfLPkBmoGN6O67wZygFuYEXkRtcw1Lt7Wg4XKKabaFjBfsJ5mmMkL3kFy7FIYt2C/f" +
            "K4/JekkM0ApDRT9RkfnnOxyTa53IhBV7+HyUJdqm+9E2CWfCXHfR4nX8PHIVSt9p0G+CJZMW+TN1SSAaMg/TFgcKhjtYZnWApZaRmG1KROlvyjgGFRAa36zo" +
            "gRW2osmuJzBxLsJycz5rNx9lqcVhQg9fwWBDOmOkD3TE82vIvpoym5qi7aqC82SDA0yRnK1qkcRs8Xg6DlnoSO8scD7CnA2pjBf+HrkwlInL9wqXR4p3D0dF" +
            "csu4heGMkl5QXr6f6SaJaFino2GbIRyXIt42gdmynL02iUWO4h1dj6Ej57dkUxYLNqSxSI6/xClD5uCILNNY5Srb7ROYZbafGUbhLD50gQUHGli4vwFdyb+G" +
            "KZ0YS942klxtmHAVy4xbrMvuZf3R+2wSbN3LXuNdJR6h9iP+Zz6JLohHqAXXU39invsE/bT7mOa+xCz/DWaCsUH2I0zznrCh5B12RcIPx54rMDeLu83coEY0" +
            "vGpR2VSC5tZ61DzOMNPjHHMDW5jqVM3CgDbUXM6gurGKGfYnmbquQLDPRG1dDqqWR5hqksQM6fupRgeZLry3Iew8W6KvKO7TbI2/ytqgckx9Cgk43IaO5LFJ" +
            "4oMXSF7XXRuHseBkJr5bY9U+NA2FmxeEMF8wUlsiOWGqE+PFy+ms3ob2Kj+UvpzML+P1KKh/KTnPg9n6oczS342mfjhaentYYBip8HlhiZ1EpN5Q3PfRd0hD" +
            "0yhCsuRhxazNs0lgvuChbZ/Mgk3ZLN2SzzzBaOGmDOaKFi90TGOB+M/Jq/cJt0fIbMYxbXUU6sbxTFp5ABXxG5o2Gcx3PIqWcE4f/tobjqAhsz/PUY7nehR9" +
            "35PoeR/H0K+U5e6yn3UCBvL9DX377oHlou99DMvgUhz3nWXzwQZ8Ettwrf7ApopXOJW+wqXitaIcTjxlbd4dTDO7sZalZVYPJmldGCa1o5/Qxur4VlbHtqB3" +
            "+BKLIs+zSq71umOPWZl4kxWJd1iT9Yz5MTdZmfaAtUVv0UuRY+Q8wSzlNtbp0ke5zwksA/MDkveibuKS8Qq7uAeYRfViHfuYBX6XWBF6TaH7usHtaLnWobGx" +
            "Eu2N5Uy3zBXeF+4zEv+yZC8aVrHoumVhGlDMD5NtcN53hu3pcj4yr8ZbMtBzTGLYTCe+HraG/hPsGCE+S23JbqbMC0VD5knH8LDMdAzLrcTP9vGF5OYhkyXH" +
            "TbJGZ8VWhqoY8ePIBSj9ZyRbduXgtE2y2mI35pmJr1qzW/Q/WpH11ZeHYLA+GdfQCtEOP+auCWe+2T4WWx9S5My5aw9KD0QzV2reeuFjh0R07GLR88hRrGut" +
            "i0F38xFmi0ebJD3VxxdqJodR0dvPZP0oNNalsHxLMSs8TwhfHGWhcI7e1lLMQ2owCihH3+8ERpJjV0iOsQipYrXgruuWw7odp3A52MiWwxfZnnWDHbndRJ54" +
            "wJ6jt4govie6fB+TAvHehXexOvkAu1PPcBAPt+nsG1xFF5zE0206/YrNdW/xaPwTX9EF/zbRhBbRgT5NuPSXLhimXWNp7GX0xMfrJoneH3sh9QyTnHs4nHyJ" +
            "04kXeJS+JuDUO7af+sDOCtGE2C6Mtp/HMqwNA+GCFX6S2yK62S294Z72Dqe4l9jsf4BT7AsWuEgG3HgKC8l5eh6n0XOtZKljoXBeJoucUoT3Q+Wa7ULpnxOl" +
            "lMUn2zHbMIBvhq9A6auZ/PPnBXw9xACt1XuFo7OYIz5cRz+GuYaioQtC0VoRzkqLODQlS2kvD2Kuri+LDXyZpmPNF79O4/N+Uxkz04DMsi7GaVqz3HoPWobB" +
            "ijy4bO0hFppHsGxdFMvXHpC8loStT7YsE7B0T8E5tEhR6wPzcNxewIbthbiGl7Mr4woBcU14HqrDObwa1/11bIlqxGnvWRzC63CIOIf1jlrsws/hFHWRzbFt" +
            "OMe04HCgCceDf62vl31swk4rll4yn54ym8FHbhCYcU1870OCMzvZfewW2zKusj2zHb+ki+zI7iD0yBUiCm8qMPRskKx27r14M8ll4tntSh9gU3IH29K7bKx6" +
            "LNr9SLHcVPsMt3PiASUXeF/4qCiPOsl1tS+wONaLcU4v64qeYVHwFPP8J1gXP8ck+7bkwpd4CK+4Fz/Ev+Q53+l48fl0F/4x1pr/jLdD6dfVKPU3FD0WrIav" +
            "Z/DiSMlv+diJDzAPvSrfS44TcJE5dkVMM0xm/LIDTF5+CJUVomGip3282qdnNttLWGi9H6XPpqCu58Xg6Sb0V14lvsyG+Wu24X/gtGS4HQyf6YrKgmDGaPow" +
            "dUEgAyfY8rdv5grWHqwSHC0dotnsk4K+uT9WG0L5eYg6//1pgujAcAL3FZJd0cO6LUlyvBq8I6sJjGuUTF3HrpSLRGZdwX3XcbYdqiHoQAX++0qxco1hjKoF" +
            "Pw5dxMTZNqgvc2XmUuEQoyDxo3txCikmMu86QQkX2JbaquCvoIwO/FLbCZSZ3ZreiXtCKy4xF3CObsItRrA/1ITT/nqZ7QZCcm6yTfbzTb6Cd1IL245cIzSn" +
            "i7Cjkm2rn5F57h0pp1+QKFksWbJaZt0r0s8+I+v8K6Lb4ZBk/b7lYcnxBztgn8x3uMz3nj4P2PCG4PpXBJx9rqi+1305MKzlE7slLwY1fxL8xduJJzQ7ehfD" +
            "zF5M8h6yJLYDs9z7WObewzLzJuuze/AoeohD0lWUfljA55Mc+GG6A/8dtYo/NG35bLQeP0yx5KuJVij93wwFh23PeYhPcg8LnfL4Vd2H/4yy5YsxtkzT28sM" +
            "yUtq+uLRHIvF+6Yp9HKsYNrn8cZobWT4DAtWrBPM9+aTcLSZ8ovPKW9+I9rtx9CZ9oyd7874BVsYP3sjc/X8+OIXHeGNcSgpDeWrH2egrm3FrDkWjJ64CN/g" +
            "eL7/bRpf95PqPwtD63Ds3NPYtK0IM89UHHYLLztG8dMkQ8zdE9ib3kDq8U7m6Xvw5a/q9Bs+n4GjFvPHqCWK+n3IPEWNmKjH/4YsRWOFD9F51wiRHOUfV8/W" +
            "uAaC0loFy2a8U9pwj78ouJ9n06F63GOb8E64pCjPuIvC602K+faKvyCvGzhU8ZDM8+85LngWX4EsyfJ9eMefusf+ouuk1TzkaONrTlz+kxLB3TqxHau4y4qy" +
            "TenAUXByEy3wKX1CYJVgLTlwu3j8UOGHvgo++x5/8f1eohNuZY8xzpC/P9oruF/H8pjox8lXkgFf41z1JzaFj3CQcj3xCOukVqIlV0wVflQasIJB2nJtxhjx" +
            "y2RD+k1ewVejF/OvIaKzP81G6beF/DzNlp+mbeCbCdai6/YoLxVszQ8yxzKa2WsiFD00RH0LyzcWor1WfJT4IXPfQoLiG0iT+Txz/QMdz6DlLrRJxi+ou03p" +
            "pVfk1D7my6HLGa65UTKfHUNlLvuyoGtwGtau4rsW2/Dd78L3/1NGa8E66YlBzFviwJq1wXz9kyr//XYyYybrob3Yga9+U0fpf6oo9ZvN4JkW/OPXWbL/MFTm" +
            "2uARegRju1BFz3zzyzQGj5jDxCkrmKSiy6RJS5kyaRnjlZcyTHzl6BmW7Eu5QGjcOUUPBCU2EpJ5Ga+ERrwSL+Eh5Slz3ffaPaZR/NtpHMNr2BRxWnxcPQ77" +
            "ajELLCAsv4fz8p2zGj8QW3GfmNJe9mS3ElXQSUrlXY6ceczZWyiq4hrkNjxT3HNbtq9VcnczJondmIuHW7lf/FdkM8aJ11gdI3ob3YZejGSbuL5McJ3VCV0Y" +
            "pPQI9uIbcu5gIDmvb9aNs+5gKHnBOKNH4R0dCu/hXNjL5mNd7Kp6SIF87pCFrnw+xoDvxxlL9jLmm8ELGSQ98M+f5Np9NVU0ejlfDVnFfwYsR+kbLYZruDJH" +
            "/LO2cSRD1V34Unyc0i9LGaTqgNveOuH7VHwPX6X5KTQ/h86PUNXznPoHr8luuEJewzWSKlopanrMiQvvFPdxf59kw+R5W4X/XRg3dyPHLz0lNOkEGwL3YeWx" +
            "jZXWco6/TuSHQbOZMceBf3w+AzNr8fgz1/HZ58Pp338EJ44Xob/GlFlLbRX68ruyHiOmGQrPG4pWqPDNr1P57H8T+O/34/jsh/Eo/Wsgv/6mwkRlHVTGa6Gt" +
            "uogZkxcyaZo+3/6mQ8Cek0RnCeYRFfK9KtmeKlwee1F6oE1RnvGXcZd8trnPH+w7J9ifZaP4hPXhZ7HeXcu63VUcEb6OF6+2t7hHMe9Jp+6SWXmfo4J7ycW3" +
            "nBZ+r+uBU519vPCUmPJuDONuon/4huB8Hf0YwTu6E4ND7RjFdGIU18ma+GuYJN/ALPUWZmnC7emSCzIEa8nua9LuYpJ5+y+Oz33A+uMvBfdbOBQ9ZZfoh/WR" +
            "Tpxy2tlW3ot7eiMz1m7ny/GrGKZpx/RlXvy3/1y+GyQzrzSc//thFt/+sYTPZfZ/El4YNNVGuGA+Y4UnRkv9a8Aqfplow2B1Z35RsUXTeC9Grkcw9SrgwmPB" +
            "/KbMeOtLSrqeUXDlFtnNHYpl2llZXnrCudugtjKQX5WtGa/pzdiZW2RO/Rih4YCpZzylbc84d+sVUbll2PruYpKOsfTATJQ+V2XSzE0MGWmGtd0BRo2aw3df" +
            "/4yfxyZGDB/M4NGaogehmNvvRmuRPYtXOjFtlhFf/6LCb8M1+WmQGoPHzuOzb0fz7y+H8uXXQxj4mzL9/zeUCWM0GD56HkPGLGO12TZ2HKok9UQPcYLftqRL" +
            "kmGlRNN9kqQXEsUHJVzBU3LXlphmRbkfbhYP2IKr9Mdm0YKU+o8Kvc8Vf3ZCNLyg4RUF559TcukNpS1vOSbkkFp1h+Squ2TUPSG76SXL9jSy+kALRpKb1xxq" +
            "VZRpTCvGhy5hsL9JUUYHhQuiWzAWv2mR3IVd1m02FjzBsfCpLB9hm3cXi/QeHMX7WWXcxCb7FqEN4JjXhX1yE/2XuTHe2I95DntZYLMb/+hqVOa7iE+bLNiP" +
            "ZMQUY/77sxbfDFrML6MN+HzQMn5WNpM5n8+vU+z5aowZQ7S20F9VcvmSQJyl710j69GyiMIupAL3qDrxN0/IvfiSk12id423ia64TOTxFiqlL441fWDRuoMM" +
            "U9vMrBW7GTnTXXGv7o/JjvwxzYXBMxyZb7KDY6fvUXv1JcVnb3K87g67Y6tYZbmX/iPWMFJ5HasMQnB03MdP34/hi399x/Ah4xkwWA0DM2+OFl1i4JBZmFh4" +
            "Mn+pNT8PmM7vI7T4dZgm3w5Q4+ehOnz92wyU/j4IpX8M4B//Hsg45fmYWfkyRVWfz76X3PL5WJQ1rKT/Uok4cpmYknuEZrUrPLx/WofC33mJD9wS24yr+MBN" +
            "hxow3VXOxtjz7Ci4SWGrcPq5N8JzH8mqfEic6H1k/mXiKm+Rfv4xafVPSK9/Rl7zW0rEG5SL/vtXvMW39AV+pS/ZVv2OHTUfCK54SWDJM4LKhBfFAwRVPCOw" +
            "/ClBlS8IrnpN4Cl5v/IN22rEE9R9IqD2HYG1H/Cv/vDXsvI1vuIN9jV+JKrpLZvTzlMmOhxVeoN/DJiD0heC+/8pK2Z+zPS1TNZcL/5blV/GrGbUrPUKf/aP" +
            "AUv4doIZ/dUd+G22M/8ZY8JS5zTRuDuC7QvCjt0VH7FSvFcFus7xGHomYx2Si29CjXy/V9Lbr2j/gMLjmHkX8t0oG6Yt2yPcv4lR6p6oLQ1lnIaP6MBOhk+X" +
            "Xphkz7hZGyg6fZ/yRvHIJztIK75KYc0jckrvERhWhv2meObOd+S7byfwy/djGSAa4eoWgaGJJ5pzzFFWWUz/3yczbeYKfvxtKl/8PJHhk3X5fdxSfhIPqPTF" +
            "OEZONeS7AeJx/j6Y/34zFvO1PljbB2K61pfdhwoYq2bEjCUbqRV9jjl+naRqwbHyMdGnHnGw/AH7JZvtPS69KZk95Jh4/sxWPFMvcKD0NifkuxZefMdx8efJ" +
            "JbfIqL5LTNkNDlf2crj6DvFnH5LT+pGSGyj8YWb9W+KEJyROsvfMByLE20Wd/8RByYOHGj4SLXk/pukTEWdes+/sGyR+yHsfCal4xNaiXvxO3FU8s+F1/D6+" +
            "JU/YInofINgEnnqO+7Gb+PXxWEkPx4V7487cJ/XsA/FHo1H670RGqK5jkrYjQyeZyfxbovS9Oj+O1meImo14QcmDAxYxfNEWvp1qxZdTrHA5XE+KnGdY8X2S" +
            "z8u1qXzFd1NsyO/judZP+CXWo78lkRUucei5pqDrlMYyh0zmWCQxbv5uRmmEMGJWEBp9z1PN28ZvE9YL9gFMm7uTCRpBqGj5SC9uQG2hC8fP3OW6+Ik+HojK" +
            "OkNMdj2Hsxo4nHaOnII2XNyihLf647xpL/UiPl4B8Zhb+6E8ZQkjx2mzNTgGJ899wm9DGDBuMQMm6vKHij5//2GGrK9CWX0tY6ebCg8MkeP8zL/+25+lK224" +
            "fP0lafkNHK3qIrOii/SqHjLO3JOsJrMrfJ1a94wkyWAJp59yWPL44VP3STvzlIj8q8L7dym8+oGi9o+ie++Jr+olp+k5abX3yDr3VDy/8P+lTxRIj2TL7EfW" +
            "PMUju51Qme3d1a/YXfOCXdXPiax7R7xod1wz7JdsH9P0nr21TwmvekRU/WsOnn/DnurH7Jae3Ce5Ypv0pVfRLexTWvGRPvAuvodb7g1CTz1hc2YbISelBwo6" +
            "2CE8VHTlT5QXufB//ecwa6Uf/ceZMEHLme+H/8X5fZ7895m2/HvUSgYvcEZp5ApGrPBmv8xASvMH9gqJRPTNgPRfgvTkQJ1NDJ27QbzObbkOj5lttpOVzolo" +
            "mEYyTMuXgao+jNTewcjZO8TnBzNeaydTFoTxx5S/dGDcbC/GqfqKvvszTcsP1TlewkUbGTJxNcEH8ul9A0dOtQovD+Rv34/kl5GauAfEYWi2lb/9bSjhESco" +
            "k74uqOwh+3gbQeFH6D9sFpM19NgZlcdA4fe/fTdOPsecIVOF4/up883vWoxXNUdlphWTVY0ZKsdUUvoOD98IbouPqblwm5qWR1y6g3DAO2pv/EnV9T8Vfr34" +
            "ygfRuNekCZ5Jpx8KN9wnsVx6pPo2ZZ0fKLvxidy2F6Q2PCSytIP4mtuk1D6goOUDlTLzZdchQ3DdU/4Ej5y+Z6wu4ZB6mc3ZXXgJn/gW32Zb2UPpg2fsqnzK" +
            "9rJ7RDW8I/z0c3aU32N7ibx/svf/q+CSO4SfeUVAyX02SE7tO4ZbrmT95Fb8haNcM67gcaQNt+QG9osXrZHvNHCWPV8MX8lowX3wjPWKZ6aUvp/Hd8oW9Jth" +
            "y79k9v8+Upd/TTRgkUcsm5POIDTHgdo7HK57TMr5F+JvuxGpZo13qnDJWJS+ncRAdSvWh+TjHFaBTXApf6ht4VcVV36f4oXKggjUdaOZOj9cdD+QGYt2oizz" +
            "Pk7y4xTtAFRm+aI2J4CpGlvQXhLA0jXbGT/LHJNNO5CPYb6Zo+jV/8SvjkVbdz39h2rTb6A2FWefcKT8NrlnHgnfXqPkwhOKznQzR28jSl8PY5SaLr+Nm0e/" +
            "0XMZqrKCP8YvFZ1XYZyKHpOmGDB52mo0tI0lG/Sn4kwnnXc+cK7tIafbHlF56QGVl59ID7yh+tpbyq68Eh/ziPTaXuLLbygq8dRN8poeUtL+mqruT+Q3PyHt" +
            "dA8JVd2kCoflX36HnB7HxQNl993nqXuDv2C0KfkyjuIp3SSj9eV8V9nmckw8etEd3PJ7cc7tljm+w47aVwqO31bxmO0y7wGig0HiSYJL7+MjfsMprRUnwXhX" +
            "7Ut2nn7Npqwu3PL6+ug+jilXcU67jEtaC355nbhItk048xINC9Fg9Y38LBnsH4NW89U4a/4+1Ji/DTWgv6bM/LczmWQajGviGeRjOCo9bb3vKHH1d4muuYlb" +
            "dBku+0/guKeIKSvdJSOIz5pmgPKCjbhFlLD/aJfMw0uSy17hsbcRHcNoBk1xY5D0whj1rdIDIUybF8zI6ZtRXxTCdG1/Js70ZLpmABNnbGHest3MXhTI7KU+" +
            "su9GzLdEiher5m/9xjFj2VoWGbuh9J8RWG3YR73waNTRdhIr7kjWukmccEBhfQ/pZZKlHQPpJ/7+ywGT+HrQVKbPt+QP5YX88JsaypNXMVZ0Ydp0faZrGPP3" +
            "r0YQFl1I10PB6kwXlRfvUnv5ETVXH3NSvOzJC3c5fuE++fW95J69zbH6e5S0vKCy8z3lwhHHhC9Sa26QUN7JEXk/r+ERRZffU9Itx+t7zrb5PTtOPRQ9vo1n" +
            "3i1cMq6zKaVLsLmBT+Vb/Gre4Vz0CKeCe2ytfovricfYZt5gc9FDAk+/x/2kaFzJU7xLnxFc84Ytgu+W4ruKWp/ejlP2demZHumde3gUynGye1if0oljegcB" +
            "oglBwgX7ap7jn3ON0SuCURpqyH9GWzJy8TaWuRcy3zmbFT75VDwAuwOSfQs7iRTty7zyHJF3Euq7cdh3hAMlF9mdc4adaVWKa913LeJPXKFdcn/Xa+G31g+c" +
            "vPiepNJediQ1kVH5jGzRtp3xrRg6JInf8GKoeP5R012YqOEtPO2D2twgZugEoaYTipr2LqkwZglPaC4LR0s0QmWBJ47b8jByjsTUdT8j1IwVPN7UIZnz4key" +
            "JXdkiq/KOH2L3LqbxBbUcSj3FJfvv2VHbBYzl4nOfz2QL35X4avfp/LlzyoMkcz3Uz9VRo5dxgR1c34cNo8163dR3fKccx1vqOt4oZj/vh6ovHSP0sZbFNZ1" +
            "k3/muixvUXrhAaevvqFe5jqttpvosiuKz88XL5J/4SnlwvNi+UiVPHRAuMmv8Bpbcq8pnq3dJNi7HOlhc0YP3kcls0uOcy1/i9OJl9hLbncqfY1d4RPMJM9Z" +
            "9d3PSezEJk/wrniPs+zT9/uv8/FnbCqWHsnqxjjuErbp11h/pJsNR27hfPShohyzb+OYdZMNqR24ZF3nUCNsTGjBbFcN4/T3stq3FMdDbaw/fAVryZoesk+u" +
            "nHe5aKBH+jkyJY+nihZeEFx7pAeSa9s4f+8D1/qe3RG8e8Xb558VritqFcxfKPj3SOVtjtaJXxcPX3H5A2kVvRzKu8zh/E6Si29yKEt4KPA4Oit3oay6RVFj" +
            "VF2ZMT+QKbOF/2eHMH9lEnN1E1BbEIn26mhmrtiFhVc2G3fks9Y3GQ09TxZKlrV0imKZ8JSNbxz+McXsPVJJ0vE6Ag4kcvHWAy703KOoronc6vPo2mxmY+AB" +
            "9iSXMkB5AfrWwagI7v1HLuKzXzSZpeslnt+ZoAMl8nfQdOM9VS0POdf5nOYe4f+WuxSd7ZK6wUnhgJLzd2V5jxON8l0lp+Y1PRYNeKd4xqlceuKofPcY8Qeh" +
            "xV345F6RWW8mQPjcv/gBG5LbcUy7jnvuXdzzHqCXfAuLvMdYF77CMv85FseesbbwJVb5rzDPk/WjLxS/365K6mFFQjcWOQ+xzH2E48l3uJ/6wEbhCDfJj075" +
            "j7BKvSnVg13WPeyP3MVOesg8thW71E68hBt2nHqNsnk081yP4ZJ4A+ekbhyT+7zHHdGKNswjy0hve6uY+cjyFo5dvkf9o3cIzfGgjweOn6asuYdeWa9tf0LB" +
            "2W6SZf6P1orWnXtIQb1cD/EJfc/1JZd1cPSceOLzDygSP1Qg2SOnUrz0iTsckCzt5n8KvbXxzFzqxxiNTYxWc2GKTjAz5oUxQ7Cfo5/IKtscbP0qsAs6iYln" +
            "GrsyGjl9/RPH+45X20Ncbh0hUdnsic/l8JEC3INDaLt5g2d/iu42nJU+uCHnd5L9uUWIlFHb/Rr3vdn4HSzBO+K45Px0Js51Ycj0dayV/Bp77DLNMrdnrrzk" +
            "ivxB9SXxlrVXOVZ9meNnr3Oq+T6nLj6iuO97nRVNOP9IcX+nXLT9ZCfkXflIwkXx5BLw3LMu4XO0E+/sDtxTrxBZ/YZ9ku9d0kSjc27je/I5mwvuszz+JroJ" +
            "Pehn3Mcg8yG6grVh9jOsij9gVvAe02NvWZp4V+o2q1LuYyb90PeMh0n2U9bmv8D62APpgcfY5Mi25JuK+4SWabcxS+7BLLEL9xPPcC9+hG1yJ0HCHcuDKvlW" +
            "yx9/6S/v3IeEVL4kpOoJcS0fSWl7w64TLUjUQdqYwktdXHr8RnHtbgoPyGYOZZdy6fYbYvNP09DznqL6+4QLX8QWXSG5tIsYyWd5Z0UrGx+Q2MeL1Z0UNN6h" +
            "WGalrz/KLnwQXhXuqPqTyNQuXHcVY+6RiOryrYzTcmfemoOs2XgUC7fjrA+swdyniODkNmLL75BV94DM6m7hk3oefoI7kg96n7zjybsP1DScp+1aGx95zcOX" +
            "vXTebSepMIPKKxcUvRuZX4bz3mTCj54jMKmKrbFV+ByqYkdio2RSfwUH9Hn+Pvxr255S2/qQ7PJmypu6qbjYK9jfpeziffGYjyi99EJxf6BB9m+4KzokOp/Z" +
            "9IGwk7fEb7dgH3+OdTFnsYk9j9sR4d/UqwQXPmDrsTvi+9rxlNn3EZ12yb6FfvodFkVfY3HsdZYn3mLuoS7mxUhPpD5kScpD9DKfsybvrfTCRxbH3VU817Hg" +
            "UDeGmU8wzXnK+uPPsZfZt8jsxVjwN029jWly37O8N1mXLsv4dqwSO3CWnvOQXhlnFS/ncJPQsk+SNZGccIuY5pdEC159+i/tTEZdG+2vPiERnLrOm9x9B/dk" +
            "5p/JemP3UwX2xQ03qbj6FBu/ONIqb5JScVOWt8k8/YCY49dIqe4lrvwaCRUd4pOF/yVT9+2XWnaHlJKH4tFekiXeIOe8HK+sk+jiTtZvz2PyYk8MXVIIF0/j" +
            "e7hJMtZzKiR/pImORp+U+RR9Wungy+8qmvS8/KTA/ur1Dk6UF/Pk1R3efLzL49ddNHedprn7PO3PuilurifhVBXFl28SfqyWuMp2IgoucSD/CtsOn2aeoT/a" +
            "4mXPtX+gouke5Y29HJXceepiDzWtd8QHPqRavuup9leSBz8htodKwbxA+vhg1VO2ZgnGsRfYcKCODZFn8Uprx+/oTTYmtWKT2MyGjA7R4i7WJlxS+HKf3Dv4" +
            "iFYH5tzFKOMeC6PbFT2wTHhgzsFraO7vRDuqi1n7r6G+7xrzYu9glPtWUSZ579BNfsSq9Mfopd3DUDjfPKsX66OPcSoR71gG1jmPFb8lrIxqxTSuE/39TTjn" +
            "3cEmSc5D9g8qeUf/pXvYeeodJQJqzs33lAr29a+g5SVcF6xLmy5x/ckTZJW27l5uPnmj6IPaK/eIPHKKoLgiVm8Oo0AAyRE+ji29QVxZj+I3r+iSvvv+z4g9" +
            "1Uti9S3S6+6SIxqZe+EZeedfklEj7x1/QIQYjojCDg6WdpLd+Ixjl15zRMin7/fRYxf/5LBk3sS+fSsfsK+ogx2ZdaRVXVPM889j1VH64kcar3ZwvLKU159e" +
            "8ObTQx6/6KTj1hmu9NTKud+j83kHRReraHn2UI55ibwLHXK+8rnHzpB3rpfIlDOU1T9mtVUgQRE5dMhMF1W30XzzOfUdwveXbnL2+jOZ9U+I5FN1q0/fP3FA" +
            "zs1b8pWneDq7g03YR0ntb8B0+ykcDl5g69F7uIn/shbvuzn/liLj2aZI1s8UTyD+3FP4eNexexil9bIq4Tq68TcUWrBA+kAnSvA/INjvbWdxwn1mhrfL6w50" +
            "Dt5kZeoTBQ8YZDwXj/BWOOAeplm3WBnfyfLoKxgJh1hIT9lmP2Zj/lOMYtoUvnCNnJNlbBsbU7pRsUtHabgNQ1bvJKntBcd6nnOpD2fxdB1vPvBIXj/9+JHz" +
            "rS1yLXsQ2Klv7ZJc3MOBrApmGThisTWGmtt9v3XeU+TdvvtfsRV3OdLwgdSzb9mZ08UhCZARRV2K+5/RFT2E5jQTmFzPziOXiSzoYZ9U3+8lITntRJX0Kn5D" +
            "2CN+cXvmBSKLutlb0K14ViY4vU3m9jqHy24TmdvCxuBUis/e4l8/jeSbQSNJLSjgyccXgvdTbj29osD+zqsWel+3cLq9nDvy3/k7V0TL7kiW7xJfcpHzN+Wz" +
            "kgoYNmExm30Osz3sCCcqr3BaZv5C50Ou3H5BVfN12h994LzoXamEnIzz99l7UvxS2kU2xJzD5kA9tjJba8MbWS9eekNcOxb7L7DucBuBwm+xwg/2ie3i97vx" +
            "yhVvntKOc2Ibm5Mu4xwr/iCjExPh6L7f8AxFt5fEdDLvwFXmRl1FJ7KdWWEtqO5uQX3PVeZEdTNL+qBvqRPZyaKYXuZFXRcf8Bzb4x+wPfFW4Rs3HH8nfuAt" +
            "1ul3MYntEA/YKxnxFg4Z3dinSQ6RnlseXMqglSGsi6zk6PV3XBV8q24/p+sjSATm9ss3Csxfvn/LpY5rdNx9rOD+i71/stQ2kH8Nns26bZmYB2Ww0iOOEMEs" +
            "Xsgjtu85lexr+KQKvifu4SX6F3ykFd+0egKONBIuHB9VIrgXCg8fvSHLu+wtui/98YJI8cfBqa2K5y78UpvxiKvDM+Es29Ilcx65JJ6tltCEerJFY/YmVLEz" +
            "8hiG5s4o/eNfjFYew8nyMh49uc+Vzgbec5dnH67S2l3GW27T+0oy6sMW7v75kDPtbZy+0k5162W0Vq5hwCgNVhi6kl14ngsdj2iVuW+6/gSxvjT1fKTs8nOi" +
            "Jet6x5fjEtM326exCqvCNOwUa/eJzof1zfxZDLfL9v2SxeI6MD/YIh7/huLf07pmiA7EX8EzXTx/qvRCXAue4gc8JLd7pF/FWrTbQGayrwd0wpswybrPvP0t" +
            "LI8TnPe1MXf/1b/qQCeaey+jvvsS2hFXZJ8uZu/pQHVXD2p7bjMnVnro2HM2VYOvfG5A5Sd8i1/idfQNVtFd2KVcF/1pVfxeMNE6nG2FbTRJljshmneouFUm" +
            "BDpfgMg7PeL53777xLs/Pyn4v+XBO8l+yOw2MFnPl36q6/h1pj2T9Hcyf1MmY4wjme2ZjVFULfq7T9J/4Wa2xJ4lWTLQjqOtbDvWjH+ucKL0QEhWG9vTZcal" +
            "93fLTGyXPLxd+nJPzi1C0ruEC3oJye/F90gzuwtbxAfU4rEzmYjYk6TkNHCyUnLYyUuUFJ/FarUJU4b8zOifvmCLpSN3JJt8evqUDy9uc6vnjJx5Fx+knrxu" +
            "5d6Li3Tdb+bhm2eSEW5yobtDtL4JAytXVpt70H7vIx2P/xRP+0zha1JOPSEw4SqOe+qw2VWFxY4TmIVKDgmuYU1ADcYyQ2YhFYr/n8Fqn0oMA2ox39WI2c56" +
            "1kZcxDHmKhuFjy3CzrFJOHjjoYvCG524Jbay4eA5fLJkFoXXzMWXmWX3YpbVxwE38Dkn/iapi7mRlxSZT3tvi+DcjGZ4i4IPZoQ2SQ+0ohUh/BB+jdmRj1Db" +
            "ew+V3e1MCLnA9J3NLD7QhW3mY7yK/2RHjXiUVth5HiJkuSmrnYP1T6mTgc6ov8W4pZtQGqCD465cLkgT3JeeeCI+4P7j94jkc+XBe9Il53mkNPF/U+34TNWZ" +
            "fnO38qWyDf8aZs4Xk9yZYZ+D0pi1/KAfgu7Okyh9NgnP2NOita+Jr7vH4XOS+cQ09d1D3lMsc1F8i/0n7xNb9ZID4gPCc3vYnXGDoKSrhEpP7JT3A3IucuBE" +
            "M3YeoahM0aQwt4TdweGEb9uLxSoTbHUNMVGbwZZlUktU0er3B26rrehpbOBuVzOf3vXy6dNNHj65oKhnr6/w6m0vV9s7Wbl6DSVVJVy/20NL1x1WWzqzyW+/" +
            "8MJTxb1Gh+1HsNx6EnPfWkz8ajHyO4Whv/idrQXoe1di4CP4Sy418S/HNKgGs+AzmAefU/zbdj2vMllWYhVaJ3WWdbvOsnb3aeGJM9juq1Pg7yueYUvfc2XC" +
            "dZbS72skE5qKV+h7hnfNkR7Fc7xLYtqF51ukF4TnD7ajGXn5L18gvnB25DVm7G5jnP85lP3PMzHwApODW5kRcoWZoVfQ2C5+IbCTmQFXWXLgNnpJD9hQ+te/" +
            "E8gR8+SbfVFyVTw/T17F9HVBDFvty99HmrExpIp20fTrUs8/9eXfRwQeOM58+3CUJpsIxmb8Z1U0SpN8+M9oe/43dgNK3xry+URvFgXX8tVCf6Y4xPGthgNO" +
            "e8vIv/SeQ9WC7akOQiquSs54TNTpuyQ2viCq9iHhpbcVv5v2/c4eXXibfXnXiam6Q3rf/ZRrbxVatD1oPxN+H8LWtSZstzdi+i+fYaM9DWPlsdhMGIX7lP74" +
            "zR7KJnVlFo3oh9vapSQn7uLs+XK6H3by6P0NOnrP033rEvVnatBfasG/lb5k/NAhZKbHcPvhTWqaLuK3IxV7twTWex/BeVspJi55WHiWYeh+HEPvYgy8s1m1" +
            "JYPVbnkYbjmJgVsFei6l6LmdxMinnDW+wgHeZdIvlZgFVGMZfBrbXfU4H7iEnncRW0UPPKS/LXeewu/INYLzbuKX3cXGkkeYS9axkBlYkyFeP6qBZXGXxd/d" +
            "ktet6OxvRTOiRXJBB3OEx1XDLjNhexMqoZeYvkt4QDRiRkgj4zwbGLm5nrHudah4XWSq9zWm+EoFtaC8tQ61nZeYue001smX+FlnI0p/n4SBczJWEWX8R82e" +
            "8QaRGHsfp1w4ovryR+y2ZjBD14cBM2Xf/81FScOJyYFlKM3bi6p3Pb9o7UDpByO+mxrEoOWH2ZD1hCHr4pnqnMZXsu9Cx8Nk1r9kb9l1xewntDwRn4lk5Hb2" +
            "lV5X+KgDZd3syqonOr+VqJxWwjPqCS84x97Cs2TWttN97xOuVptYOVEZL90ZJLvpUbV/C7O/V0L/jx+xGPwDPhO/I2bJcDyn/4jp2P+wcNTf0J31OwGeDqSm" +
            "JdDSfp7n7x/Q0X2Z2ZrazNc2ItAjkjGDJvL9F9+zVHc+ETHRWNkG4+iaivG6OPTXJWG6MRszl3wMNx3D2KMIE6889N1S0XdNl/UCjDdXsGpj332pCoy9SjHa" +
            "IvrgW4FdSB0W0gMG0jdmW8sw9StnhdtR9pc8Z5/UBgnd/pIBgrJv4JV8GRvJBevkwhhltIsOdGMq/LdKssHCmBaMjtxmVer/P/9aUR3MiugQvb+CqnhB1bBW" +
            "weQ004IamBrQgYpvJ+M8Ghm1+SyjHesZ7VTHBPfTDF9fzGTPatbEd/PZHA+U/qsuOn4QTctMflQPYOzqCHbl3+SicH+TeMBpVhEofT6bQfMC6Kcbxg96EcyR" +
            "fv5m4S6UBq/l71M9UXerYaBxDl/MPYDS9ADJLZ387pzFILs4BptGMMl0DzXiGTJb3pMtHiO37/dR4ZXDJdfJlDeOnXtKxaWXVDbfpqnzERc7XtJ66x1XXnyk" +
            "+dFrZBMfxYS66puxbsZY0jYv4+S2NVxICMJ4XD9m/FsJl2kDqXVfQrGVMpWO08i3HcP6UUqsG6dEqOkiHHWXsccvkAsXLnC1t4P8mnJWmjsxXdMSE7NwdJd7" +
            "8p/PfkRVXQtP7/2Ymu3EyjqWdQ5ZmFinY7bhKIb2+Zg5H8d8cwFrnDME9yQsPbKwcjuBsdNxjJzzMfcswtQtH9MtBawTzC28TmAo6yaexVgHVbJ+5xkcdp/B" +
            "1P8kdruqxdu24Jt8VXriqcx+l6IM0q6gn3KV1cntLE9oY+GhZoUOLI+7hva+SwoPoLPvsiIX9Gl/3zxP8DuL2o5Gpgc3MkVmfpJrExOc6xi36QwjN9Yy3OGU" +
            "Avvxm44zem0O/XWjUPr3AgYuDudnrXC+Uw1FwyqHQ+UfFff7Yk/f4x8qpihNsGSsZRz99CKZ6lfF1K01KE304ks1f75X3YJSPyOGWR1FRTjnn/MOoqS5i4k7" +
            "GlCPauFrkwMMsjjE0JWhHL8BeR2QKjqQ0PAcifoUSpOd7UJ8eN+9hHdcvv2B7seiOaJLXbJsEQ9W3/tS8pdkEekHM3U1QvRUSbabxpXY9STYLxeuH8nWJWPZ" +
            "tmw4e5cOYfNIJTykUnV/oMVXjaQVP5NkPJ1tS9WxUFfHdZ0tp+vKuf1C8sG9F9i5HUR58gZ0+/6d2MotfPbP31m5zBYrywBMTHdhLudvZpUgfZCLpeBv5VSM" +
            "pVMepo6pmDjFYO6cgLlTLiYbjmGwPg0770LW+xZj7porfZKHjc9x7AMqsA86pegN++3iH/1OYul/ApfIetyiGnE/2ERQagdr829hmXdLwf8rEy4r/j2gXoLk" +
            "+NTrLJQsOS+igeVRbRjE32Dp/iuoic9U8z/DIskCqw73MnPnZaYHXUDDqwYttyrmbBYcXBoZ41jJwA2F/G6bxQjbDLQcC1D6yoh/DfFBeUUuPy+OZrxVOgni" +
            "CRpFaO12FKH06xyUfprDsFUhDNyUzPjdNQwVXvvbnBD+q7qV3+fs4LMJmxT3Dr7UCmG62yn+qbUXJZ0IPjNPRSeuix+tkhlpm8YP2v6U9t0fEJwlclAk3rLi" +
            "/l/PQFR0Ssn2GnldJlpTK8szfT0hTVhyDSplvfsJPJX9V08ZTrb7ImLW9OdJtjPBc8by/0g66/goz6wNT1tKobgFTUKIu3symdjEk4lMJu7u7p5AcHd3K+7u" +
            "boVCobRQqLfb7Xa73fZbv75D+sfzm3lfkiGZc859X/fMvE+afGawMGkKP55qJN16CDrDIezND6XLbgjrVMN4UOXEzXI7tiYYs6MwjvrwQLxMJrNx5TK++eZn" +
            "Xn/9X2bNPoCnaxFBHi1kauYxarAxeZm11FUvpqRwKcXFmyir2ENxxW4Ky3eRV76d7PINZFWuJrtyI7kVh8irOE5581HKmvdTKf5Q3n6YwqYDFDQfpkg8v6Dt" +
            "BBV9FyjsOi29cGGArwp6z1Lcf572dY+YLXkwTfJP7qFvSd75ObHrP6bkyJ9J3vIZ0cs/HPj895v3dGIW3cO5+gCTtMsZGT6HaUmrsJeaOlYcRiX8FzLrPnE9" +
            "V4htOYe68jJe5ZdwrL+AfdslrES7HKUPYuR4uGkjtv5b8Uo+wbiQxRSsFR3a+ARzP/H4kV6yvFFYJmNeuArlxocMqdrFIO0qDDN2CPPPY5JvN6PcmhnkUodi" +
            "kg5V5xUmJGzhrdCVDBcvSTz4C2/HLCN0zn08S/ZTvEJyz4bPJP9+SqVo2pvPyfbufDHA+bNE5+bJ7z5n12f07/iMmZs+ZY7kn86tnwz821rJzcvm7yfF3ZiT" +
            "XaEcKJsBZ5qZqTKmQzmVrSUT+NOpXEq9J5BuMYnZIa6s0zizNXo6h3T6XCux5kKxAztSnGjyNiTf3RyzIQpKtCncOneHu1c/ZcHMAwTZt1GWuJEoZSpO5q5U" +
            "F80kS9dDfdk2inI2Ulywgdz8leQUrCCvdAWF1csprt9AReMR6trOUF63m9jkfhIzFlDTtp/G3pMUSz8U1O8jv0l6RLy/sE30oecs5b3nqJl3mSrJjdXzLrBg" +
            "16fk7PuBlO1fkH/oT6TveE3ObtGDHV+QvuFT4hfdxa54G+PDZzFW3YNxwiIMImczzr+NqeLN5pqFmEbMxzCgB4eExQPXonoV78e34TQuTcexqj6KdZl4QNop" +
            "EpoeMNqqg0mO4tUFh7CMXUh80z5GGGsHPmOteNdx4HOBdj27iD/1JUOLNjMsawujtBsYEbCQ9x2aGWHfwGifVskBhSQJowYvuY9COFAv6wMUkYvJuQDjcnbh" +
            "3XGd2J4HBFScJqzpEqGN5+X2HHHC06ktwsjCzJkNf6wU8ab0eslMNcJK0qNJtSfQFO2muOYouohqGuNUnJ8VzflWW37Zn0ez60i6VKM522fITydiyZqhoD/E" +
            "jmpHPZo8J7E62YEDpSpWRFhwKN+Xc1UuLAkbReGMd1C+oyDOTJ9oO3vm1nWxY+kREryaKYyZh9olkkBXNdWZMylNWUx5unBAylpKc9ZQKDzU1LiJxra1UvMl" +
            "pBd2EZHQindAlfhIOm7e2fj4FxMU0TjQB8X1O6jpODKgB9kyQ6VtR6jsPUVFz0mqZ56iZeFlupbfoGflrYHr8FM2f07e3u8H9vrL3/EVIb2XsczeyCSp9dSo" +
            "2RjKMo7qxyC4g6mqRiwj+7CL7me6XwMT7IuY4lTB++a5MpNxjPSswjZzOeqOw6glj7iVXMI9/yreeRcwUq9EYZQ3sDeFb+oizAJqGGQYLd8num+soXLvE6L2" +
            "PESRJ5oevQSTqgtMyzrK0KDlov89KCTzORbuoOXsb8Ijt1H4Cgv49aKXu4eSG6Da8Jpxufuwr75I8Zqf8M0/h3P6YZzSDuCWth9l5iFC0w8RnryXiKRdROp2" +
            "EZW0W9ZeohN2o3lznLKRsPglZGduJsY3nx0ttWwr9eHzjQn850wZy7XTWZwwgRVpCv5yLJR/HK1gS7I5vUGjqHZ/l3yHIeTbjOJkQx47s9RsTTTlSq03i1QT" +
            "iH9fQcKUQURMG4vr2EnUJJSRF1xNfVIfbtNc0fpqqU7spjyunyLNXBpz1tFSItkguZeokFI8POKYqG+JnZs/XgE6vPzSCA4tJChEfCSkhAB1KT6B5fiqq4kW" +
            "Tcit3ERl+wEaRBOquo5S3X2EptmnqZ91jJZ5p1my9QE1R/5G3bG/D9yq2s9hmLyaSdHzmR63gOnRovXqLqb4t8iMt2Cubsc2tBOniG5sApsHrsfQc5HlXDqw" +
            "H5WeQyljbJLFxwNllj0Z7VCOe/pu1LU38Cy7iGnyVvm3GIKKV/C+QSyKt1xRjPBhnKaNajFr25adKKZrsCnbi3n5FQyKbmPV8JTRGYcZkrRiYJ+Cpov/RV/Y" +
            "WGFRKtzfilPnVeqEISbWH0cRvpDBSVuZkSt+0/aMkNLbWEfvwzpyD3YRu/CI2ENA6G6Cg7ai9t9EqN9aIgNWExOwnDjVcuIDRZPCZhId3EK2ph+NYwqHOmdy" +
            "c14lW/NNeLk1QWBlDo/XaViRoWBxrIJnC53gRjm3ZpvQF6lgZoQxzUobqpxM2ZAczc7kWGZ5zGB3pg37Cu1QD1OQaa1HnKkFPuNMqQhNpz6uBJfRNvRmdVMW" +
            "WkdJWB2t6d3UJjVTldSGyiaWGeO8MJ3miY2VKxY2tnj4qvAPjsbTS5ZnPCr/TEIjSgiPriUorBL/8FqCNe0kCE8WN2yjtms/bXOOM2e5zP78E7TPOcrSjTfI" +
            "XvcptoU7mRA5n6F+7UwK72eqPAeTgruY4NuEQWALU7yrZdbrsQ/vxCGklRnuZUyxy2ayXS5TpPajLHMYa1WAvsuba3ZEy4faCed7oGeTj0bYRTE5Gbu49aiF" +
            "VxVj1eipSlCMFr8f441DxaqBfaYmFq2Qmqah0E/HPHMfxiVX8JjzDZOr76LfcIuUE/+k+Mr/ULg0oPDuRC9/L4ELnhAqOUTh1SVa0M2gpM3Y1F5F2XAfw8Dt" +
            "WAXvwzn6uPTrIZxC9+MZspegoN2E+G0nzHcTkb7r0fiuROe3ggzVSrL8F5Ae2k1iYDXZYc0kOqdyauZq7i+ZRVfQWHrDFDxYEyUhtQ1uV3Krz5idOQo2pCv4" +
            "0wElfz2VzNoMKzbnhnC4KoVqOwOWhYSwLlpNhp6CzemW3FuYTuhIBf6SH/1HDiPe1BStlTP+4xzo1bbQk9jMgvx2ysM1WAwZitXw8ThMssDZ0AM3cyWWRjY4" +
            "2DliZWWFrzKAoMBYAv0T8fFJkF5IRBWYQ1hUFYERVXj4F+EdVE5YXCtxqX2k5s2jtnUrsxcfZ9nqCyxfe4GYrrOY6ZbKrM8dmHfL+EVMC+pkkt8ftddzL5e5" +
            "bxvYM8jIp5qJdjmMt85EzzqLiVZ5DNfPGbh+YqRxHAo9TxQGUtf3bQiv2Iy2XuZ5uP0f19QOCWXyhEyGT81g2Js+meSE09yN9D3+hfcia0UXxAeGRTNIvUFm" +
            "+jMMmq5hJLlPc/BX8i6B96LvULgvRBGxFMfOy6jmfchojeTJKYUM8+xnrOQJha/wSNJhYZJ9eGrO4Bt9Bo+oUziHigdIL7gG7sTXbxv+vhsJVK6X+V9PpN86" +
            "EkQHsgLWk66cT3pQB4nKCrLVtWR4ZZFqGcDm/EJWp3uzrciGdZl6HK6cCkfS4Vkb3xwKZ2P1cPoTpDdWBPGvsw08W5HK6ZogrrUm0egwniKjURToTyRGar4s" +
            "1gguzSLXUEG48GC2lR7pltbk2YTQHpRP5ERL/MeM4On+RTQn2mEjemE7RoHjRD0cp5oT4qbGdIo1VjMcsRVe9HYPxt8vCpVfLG7ukTi6ih4IS/qHFOIfVk6o" +
            "pg43ZQ6q0DIS07rQJreRktlJY8saFiw+SIzkBYfkRZhE9TLOq1oYu47Jynqm+NYO1N40qAWbN7WX+R9jk804mfmpLiVMdChgnEU2U0wLGTpBtHyw8NtwG5lr" +
            "J3Tt23FO6EcxSPR9sCWDB1sxWOHI+4P9RRcCUFglUXLmEZqdJ0UDbEUf5Ny0RIb5zsK44jbTmx8zsvo4KZf/Q9Y1MJ91C0XQGiYXS8bY8D1Rkj0H+c1EYVLJ" +
            "CJsOhtt0Msh5JgaaHdjrTuARfwYX1QHho2Mow6UH1EdxDdiLh2oH/j6bCfZeR4jPaqn9GiKVK4n3WU6m7zKSXbpJE6ZI9i4nO6iK/MAiSn1TKHT2o9bPie4I" +
            "cxYlzWBbznQ2RA3mi+VB8LSFH85ksDj5PTpUCvbkT+NfxyrgeheHS2252BHEnHAjwgZLvaX+0aMUdPm9xz+OdNKnGkOYaEGl21S0eoYkTrDEV/E+wcPfJUC+" +
            "7vKKIvb0xuE1WYH1CAX2eqPwNHZAae2Pq5kn5tNsMBYesDBzxMMtiICgePwDk/BQJuKtSicwvJAQTaWwYi2RcTXCiJn4BWRTVj6P/tnbaWxeilfGYibK7/tm" +
            "6SurMVU3M823CqOAxoFr7WwjezBSNTDKLm9gvdn7dqpXFe9bZvG+kZZx06MYNikIxTs2jHPKGNhLd5pbMe+Ok3ODPGT23RhvlCh8by39YclwvxTWPfoR10Lh" +
            "uffM5fuscFBVCxdmoHCqZ2LJIUx6btEqnhCw+3smtp/FfuUj7Jc9Imb3n7GpPodiQjYTXbqYbN/KeKt2xjr1Y67di0PmSey0x3AVzfcLPi4+f1q46BSqoCPi" +
            "jx+g9tspTLeFeKm/1msVOt/lJPguQOc9i3zfPrJdmin26yBfft839c8NLqY4OJuKkBTylGEUKJXkuJsxK9aefQWurIkaxeFiQ/51Ih+uVHGqcgYXa105U23L" +
            "N9si4VY+p3pNWV80jf54E5ltBcmSA2ImKcgyGcL9pQWc6HCkyE7Oy7nQt98ma4op6rdHoFKIP4g+LMhw4vb2cgKNRQPGK5g+eKj0gSVWE8xwMXPGzd4dayuH" +
            "AS5wdg3Ay1eDu2+8LOFD//SBz5f7BmUToSknLqmGgMBMvLwSaG5ewpMnf8Y6og2ToHqswtuZ7FWOgaoKc/G+N3sdm4c0Cd+VMMW9UhivnAku5VL7WkbaFcu8" +
            "Jg/wvmKkzLRCejJ9PrHV20WP43l7ggbF275yXskYiwrxBPH1Sd7Yl/ew8s5rhrjK973rJFqhwtGjAMUoFQqzHEYlrCBt/880fAiT6s+iiFnB5KYzqDa/wH31" +
            "fQanSH4wlqwh9R5l0sh4Y/Eot/n4ZJzEImE/jmlnsI05ilvUSXyCjhIstQ8MPoZ/wAGCVcL7qu0kKDeT6L2WJK/lJPsslh7oJ0V4olTZTalnE83CPtXqVnID" +
            "ykn3yyVfnU9eYBbV0WUUqHSUB0ZR7OEofDeZ3blerNFMpVnq98nCYDjfwPMFsXyxOoGDFaM53joa7uVxe6UXc3UTqPQZQZT0QNBoqfW4t/CR+h7vduPr/anM" +
            "C5tArOSDrAmjSBw5ljQ9AwLeVRA8Vs65DuHpkSYyfIZjN1TOzZiO9ftj8DaywstafFSWjZkD5qZO2DuqJBfESQ5IFN3X4u6XKB5QKNybhV9gGtGaYmKi8ikU" +
            "xtix5Qzmwa2YiM+bh3QxPbgN86gOrIQbTcT/pvmUMtm5jIn2pbLKB9bgGdkoxktmN5C6TciRuhahKTmGfdx80fIghkyVf3svlKHTihhqJVldNFXhVEvh3o+p" +
            "2Xl/YC8exRCpt34mJgnLGestvTTUmUnaefTegfLTMCh2GwqHPrndgmbbD1i2SC9Eip+4N6FwbOdt807JnQtxCN+DV+YZHFNOYB1/FOvYE7jGXsA79hK+Uefw" +
            "DTsuHHxUcpGwonIrEarNxCk3oFWtI0W9SdhvEamBsnx6KFd1U+haRZFoV6MweGVYFRlKqb/krvzgEkoDSqgOLKQ5KIPmQA1VHu4UC991vHmvJ3AizZL9dhfY" +
            "8NcPSvjfmWqOVxswR61gY9q7/OdCMV/ulO9TDibfejDJRkOImfwWblJfb9H5xRnWcLGTbWnTSJd6N9vrET/6PSJGDydQVoTBMNTiARcWJbM4VfRBPCNCbyRB" +
            "Ew1wnWiInWiGr60H9qYOWJg44ekdiqt3GAERKbj5aYQFMwiOzCQ4PIuwsFySE2tJ09axsH8nZuLvkz0qMA/twC6+H9PwDuGnNizCJfP5VmMV2CY6W0pg0iqm" +
            "Ogun6ekG/uaAYowWm7g1JNRfYZRDozCfZL6Rovni8RPNihlqIpo+SXrER1j2wm/YlQurjZJsMCyAUUbZ2GbuxiBH6jwhkFHRjSy4B54tZ1CIpisClmFdfpni" +
            "ff9mbOImhkUvZYpuLaOC37z/U8sotyV4pV/GM+0KDkmnsNWdFt0/h1PCRVzj5bzmEu6Rp4X3D6EKk9kP3014yFbCJesFecwnwHUm4T7ziPKeSax3L2l+3aS7" +
            "S+7yq6NOLbc+uZT5FVARVEKJfzFlUvuqgGLqVAW0BuXSKZmtMzyBzrAQCuz0xb8n0+g9kdlh+sIGVny5JZ1/HCtmheZtZvkrWBKt4Kf9BXy9s5TuoAmki5a3" +
            "B5mRaj4K7+HCBFMVlDso+HpTGtfaAsg1UJBnPp4EI31iTczxHP0+CRYjUEqvLEgy40B9OFnGYwgcOYRwEyOcJ0/Bw8gcfwcPrA2tsbNww8szRLwgUuY+ASeP" +
            "IByFE6M02Wii8wgLziJcckJt2QKMAxvQ9y3DKqJOntN+ycqd0g/VA3vLG/u0MdGxjJHi9W/2E3aVbKAYFy89EEdC20GCqoW5bROk7sJ5wzyxl3ygzVw/sL+1" +
            "d+YGTFMWE7f2ChGLD4u+h8nMBzPZJVfmV7zeo56h2kUo552m69qvjAhqQ2GYIbf9xC58RMvZ/8pjV4kONGCfvRvDqNUMcuzBMvkAXkU3sIqXeiddlZ49i4P2" +
            "PI66KzjLsaP2Ks6xF/GMOI1fyBH8/XcQEriFwKCVKFVzRQtmoYmZR2rEHHT+neRK5olwqyA1SPQ1qJZUr3yqQ6uoCyiiUXqg3b9Qsl8RHSFSe3UWrSFS+8hU" +
            "ZsVlMDcxhd7oUJoD7Chz1WNpkhNXZ2axSmfOoUqZ6St13OjzoE96oNFVwekmFV/vKqDWXUGLzzi25oeRbjiaSJnnOJnvWMmIJ+sD+aDMHz/pC6+x7+M9bSJe" +
            "+hPwmTIS11Fv4zluMNluZnxQl0lftAuewgT+pqOwGDGIxoxsIl2D8RU+dJjhiY9rCEH+kcTHJRETHU+EZEqlTxhqYcTYyBK0MdXCefXi941M9snDJLgKp9h+" +
            "ef4WYaTsEd9vJrJ8G4PNU4TdlH+8Dz85itYN90ls2Sj1dBducxK9yGD+9hv88B94/hN8C5x4DSfffNbj7DPMk2sw8EwU/3izp4uLPI4/o5MXDnxu1LfvlPSP" +
            "+MGoYCZF9DHv4j+InX1ZHjsJhWk+w70l19uJRvmvxC3zNPbpZzGKPY6l9hIuaXelB27irLuOU+I1HBMvS+64iFvsOXxiTon+HRHe3SLMsxJf1UychGOUAdVE" +
            "RzUSLHV2M48j0lu8MLFLdCGDOJnx3PAasn3yaVSVMDOojFmqfOYJA8yNzKU3SmoflUxXtI6+mGRmx+pYoNMxTxdJT7SneII+e8pj2VkSTImtglalgl+OVPLj" +
            "3hIWRY0jZ7qC/tB3+GFvIYervOhW6dHpY0Ta1KH4v6MgXm8Qda5jOdisZW5mjHCHD/mxavITwsnThGExeiRTFO9gMXiQ+MFgjs8uZnt3Kk7SA0rDYRSHBxNg" +
            "YkOorQ8RLmo8zFzwsXdDExxGsI8vgX4qIoJjiAqT+kf9oQMzhPtMwtqZqqzDOX6+cOBMpilbGe5QhsJcPDquj7cchN+MwgmsWMKBh7+SUL2IETN88UsoZt3h" +
            "iwPXyL6SdfLVPzgh3L7m6o+svv4XMucd4n3bKNEMx4F9kIaa+EldRSvia+kVxlN1nRdeFP0YrsK/8wA9V/6CSYZwhImwpZ6cn5TJO1bCokkHsE87jUH4fqwT" +
            "zuOUeh2rhEs4pNzBKfkBTrp7A3Pvor2Aj+iBMvYkyqgj+ER+gFKzFSvPNvHDSqIiakiMKsPTMYwpU4WZbf3xcAgmOiCJdG05iWGFBNvHUxZWTVNgOR3eBfT7" +
            "5bFA+Hl+ZDpzo5OYEyf1TtCxMF7H/FgtC3UptIWFSh9opT8CafQ3pT/WlkqX8TR4CfM5Kvh0Qwa/n6pkR74B8ZLjSkwUPF8ew1ebUyi2UaARHmywNiN5zFgi" +
            "xr5FW4wXnxzdy48f3efqif08uHORVy8e09/RSKynK/ajh2Eq7GApLLi5PZ0b2zsJMHwHI8kMHuPepyE+ho7MdEKtrNB6+1Cq1aFR+aN0ckPtE4zKSy21TyA+" +
            "Jgv90FaMwrvQ928f2Ht2vEul+OpSUnuPoa7fgsJVeG+6mspdt1h+82tUJT0E5bWz/fRTfnpzDcy/4OU/Yffjr9n59Cd2Pv47G279KL2yGMVUXwYbqNB70wND" +
            "JetNV5G8+RKVF75B4ScsYZ4lOSKBsrUP6Dr1HQq7VJl53UDfKaZkoec5F9eMUzimX8A09ozU/Ao+2Y9xTb6NWcxZ6Ysb2CfeFf2/I3N/HQ+ZfWXCaan/QTwi" +
            "t+IcthxrVad4QTuRsY2EBuegi8kjJSEHB2cfTEytiAkKpSw9Dz/J+ClRmeTElBLrpiPXLZ3usAphuEIWh+SwJDyNpZGJLI2LZ7kukeXJWpZIzZckpwz0QL9G" +
            "y4ZC0YlEf+kHO4ocpxErma7afQy55gr219jx86FSjla4kyrzWmqq4HqXM19vzmZ1gilaYf9S8ylEThqGm2h/nsqFwyvm8sXDW9y6dpazl49z7eY5zh7YwaLW" +
            "KtF5c8zl6yZLzRsTXPjx1l4K/M0wlOM4mwnoHA3wnzacSKvpwjeeJHh5E+rsgVYdRaiPmkBhxBD/GMyS5jBNmG+sRwOeySsHrsl+k82mq0vo3P+A/otf0n7u" +
            "FRUH7+PbvpqFN15x+S8M7Ktz+un/uCN6f/7bf7D4yku2fPgrs3Z/zDTPXKm35P0p4g96ovfv2/JWePnA3xOJbxW9HxoqM56IV8tO9r4ETcteFCMCJFdInxjL" +
            "91rXYpF2EJfSWxglXsAo/ho2Cfexj78vefUKjnHXcUu7jUOy+H/iG+2/gbv2Jl7SHx5ayX/xW3AWXnGLbiE8qRUPZQYBIfno0hvJKWnHPzSOSZMm4WxhQlqw" +
            "H7PLiqlKSUPrH47KyZ+SpAqK48pI8YinPbSA+SG5rAnJYH2ojo1R8azXxrIiNYYlsjYX5LA6I50F8bGszhYmiItlYUoM6wpjKfeYTLrZECrd9ci3HESbchh/" +
            "P17Bhwv82JKqR4OVgrmeg/h2XQ7n+3yJFn+IMnhflh7BkhMDprzFJpn5J1cucPf+Fc7dPM3mDzZw5uweNi+qpTMvhDRfS8wHS540G8G9PXOYlSU5W3Llqqow" +
            "zi+vY1dnAdm+toRZGKNxcycjTEOYRwBBboFoguLQj+/Gv24bUwIacIqbSYDkeEPv3D9et53qTVjnFop23yV33z0uvLkO93toPP458y78yqLzP7Pq6rfMPfmU" +
            "nkNPyFp8aoDx3nNOYYhd3B/vBYx2I6h2A113f2NwlGTBweHiBzqyl95j/rXfGBcoOjDER5ggjvcsKpkRsRaLlMMYJJ1FP+U6Rkn3pUcfY534VBj/iawHA/Pu" +
            "knQLz9TbogU38Ui5gWfSNVwTT+IUvxWHuEW4altRJlbgEZxEVEIBmYVtxKZVoUktJzQ6BTMTSxyN5Tlxd6IrO40aqV9/SQUlmmQi3QKI8QqjRlckmUB6QPxh" +
            "cXCK1D+JzdEJbE3QsC4tilVpkWzJSWZDho61GQks1kaxLjtL/CGW+clqVhdE0R3lSp7deKqFDbLNFCQK551pc4arfVxrVlEkzFdiqOBsvwsPt6RS5DyGGL23" +
            "KXIYQZBkQS9h/vwQH86LHxw9vlfqv44de5exdkEp8+q09Jen0VuYjK/BUJzHSN1rY3j4QTdX11SyKN8Xb3mMN74QYT6ZJE+XgR4Ic/Ii1jeMCM8wbEtFp92F" +
            "74ZZ8bZpMNnNm1iw9T7hOQvxzZpLxbbrdF36iv6P/sk6mfuMQ98Tuf45sRs+I37ZQ8rXPh7Yj2qysgTFWBcsmpZQceUFCgthPeNEKtc/Zs6bvZrek9w/SWrv" +
            "Vsjsy/+gect3vGteKT2ilV6LEG8oxiR2O25FNzEVfzeIv4lV2nPM459ilficGVJ7o4RHmKc+xib5ofTqXckkt/EW/XdNvIh96hFs0zbjlrEU7/QePDQleIpn" +
            "x+QK25c3ok7IRJNSQFFx6wD/ztB3wcHci/SwOFozcmiM19IaG8+c9AwWFxQRYWlFuq+K8vBY2uPSaAuKZUGEjmVyvCs1lc2JUezOihtYO7I0rNUFsTU3lnVy" +
            "PD8+nFVZqSzNiGdlroY1BRFUek0hboqCZH0FWqn5/IgJkvlbudXnSL7MfbLM+xz1VP62r4nFEeOJkboXChvkOA1CazMcn6lj2bZwHh9sX8rG9b0snVdJa6WW" +
            "+rw42qX+G/tqqU0MwnWCgopwB9yFKdwkVzgPU5DiPJbu9EBSPM2Id7UlyduPGNcAIpwDqTjwCKfKhYwVFhzprmWMYxL+ogH9uz5l5YVf6Dv7JckbrpC040Oy" +
            "jnxN2LpnuM28iUPbGbybT6As2C7ZoU7qnUDq1is0v/od1e6rOLTvoefC33ErkYw/TrLfpCh8Mxex6gnYFG8S5k/hfbMW0fwsJgUsxKPoPO7Fd0T3b2Oa9AAL" +
            "7VPM4p5hGvMxpglPsUz7DJOUJxjG38Uy5RGemZ/invIRflmP8cm6jLVuM87Zy3DP6sIxoUh0rILI3GpiCmuJzColt7KVjOwaslLqsDNR426XjLtNIpaT3WjK" +
            "rKNVm8P81GzmxcexUBvNhtIc6kL90DlYUxUexuzUDCq8VaxMSmdRSDg7tAnsT4njUE609EMgJyt17MgOZ2d+LEsS1WzMFy+Ij5Ye0LC+JJ49DcKMOg+Sp7+L" +
            "VrjgDfflCxd8sjKU/ztaxhzVBAqlN5okNzxfEMOD7jCy5etW6Sbyl5PdzNT5YSU6X5seytl9q+hrKKYmT0dbWTq1OXE0ZseztKmUshh/MpW25PhZEzpjGMsr" +
            "E2hN9MZ9nAKr9xRYiDfkq/2p0MSRqgzEv2cv2VtuUnfgCdG9+6nb+YyFl36jdtdrcjd9TNzKm6jmniRhy4eELr2MT+dRTLNXYJ21FMv4mZLbAsXj1SRsukmS" +
            "9ErYiVcE7v2YtP2iAUrJEKNF2yf6UbL5Bouv/yxZogDFmDd7cqczznMh1unHsCu4ik3uLWZIhjPRCM/pPsU5+bU8/nNMRfenJzzAXPuh8P5Hcv4JtskfYZb+" +
            "BJOsh5ilXpZMeITY+qN4p83EWJ0ivVGEuqKBqGrhvtx6NGk1JKfWEKhMY/oUFTYmWlKiFwoL9eNhnoGN5FGdWzxtku+WSZ5bnRjOhqRQtuVoWJQSToqdETWR" +
            "gezqaqU5RM2yuESO5RSxRxPJqbxwdie5szdNzc70cLZnRQy897siNYTNRcksz4hjc0kGq/NihA8j2FQURrWnHskGCjr9JpMhmrAr05Z/H2nlgzwbcuR8iszt" +
            "42Zfvp8TzO0aU57MD+CXS7PYNycbG2H+MAsjdvYvYk3HTCpT4yjQBtIpffCmB+qFPQrC/cj0d6dcfubSMBXT3ryXoD+GKGtD1jQVc2PnKq5vX8b69lKiF17A" +
            "s+kD6o5+z/w7UH/iL2Rs/pSo5ffwn38V5TzR1tb9eHUdxlt4TVm7DmutZMbgChRv24l2ezHINplJVatx2faAANGIuHN/wWzmUdH/SEaEVrH7BWQuP/fH6/xD" +
            "hP0m5WIYuQn7nPM4lj3EJPc+hqn3/9B23XOsNM+wjnkmbPcpM+IeYiH1ttbex0o8wTHlHg5ZH2KWKz1RKPpffxOf8kNMVNZjL4yvbV5EaEUzfoWV+OdWklDU" +
            "TXr+HBwlWxiJBnk7VgjnS74JW0GS/xLywlaKL7bhMi6AwKmuLEjLYm2WlmUxPmxPD2FHbhQ7S5OpC3Qh18WKTSV5bMnOZpEqkD0JUexL8uH5rDzRAx8O50eL" +
            "H4RypCqJg9VJ8jjhrMmMZUd5FltLdWwqjuFoeypnejJZEOdCovhArv47A58LWqgez3+PtfPxwhR6nCULSA8ciFLwtxXB/H1bHF/vTObZjiL6Ez0wk3q6DB1L" +
            "T3o+i+srKEsMpbskhfaCJPKj/IVpHMgNV5PiK5nG25/icA3Jnj6cWbOcr6+dZGNHIbv7i7i1qw/vhiOsegwi5+Tv+StRq5/jOfcKgWvuYCU1N2s8wPTijbjX" +
            "b0dVtw6r2AremuY+sFelnmk0b00OGXjtz2XeKbLu/hO/va8p/BiCTn/D9P5DNJ//E+PfvL83VPh+gmQLy2ps8o9hXXYDs7IH6KXcxihTPD7nJZaZLzFPeS71" +
            "/hRL7Sfyfz3BKkLyfYLUP+EO5rrrWOTI9+VewjbvgnDrVayTlzNd041H2Xq8ytfiWTiPsIo5RBY2kV3bT6D8vOOnhOPk1CK5bx/lGeeJcFtGbuRGMnwXUuK9" +
            "ijKfpVRGzENtFYfNaEPJ/yGsFk9dlxbOtqRg9qaGc7QgkRURSkrMJrNUE8SphhKZW43Mvj/nSyI5khsg9Q/mfG3KgAZszY5gX4WOfVWSGZOVbC3ScKhBMkRW" +
            "EJvygjjZksqR+kQa3PRJkjyYKr5dJPp/p8keTmWxJkzBNq2CRz3ufLlYzV83hnKnw5lO5SRKnYwIHv8uHsPfIs/PjVkFqSxvLKa7QEtNqviG/JxZEaHkRMRS" +
            "pMkg2llNbkgisS6ewpK+fDCvhi1dOta1hpOx7Bkr70H2hm8Jnv0hbl0XsWs/jk3bfqYUrUY/bzWGGcvwrd3CKL98mWE7xjnFSK5/U09hOv1Qqj/4jPT9X2Lc" +
            "c46ky//G/9iPhF79Gd/dHw3s9a0YKyz4vg5j7yX4VP5xbYhp9YdMKrjLjLwXGGe8YLruqWSRRxiJvltnv8Qm/SUWwn5m6vtYht/HJ0/OZd5hfMwevOovE9Zx" +
            "iXEBLcyIbcclfwHeNesIaN6KZ/ESwsqXoKtYiKlzEsPHhRARvYxU3XECPLeSEnqCtMAPKIzaSW3MLqqUm8TXV5Pl3kutdh46yYrWo/SJnGEs+S6WPXladiUH" +
            "czpPw8USLddqM+hwnka39wxOVOm4VJPGqmBbDmSquFgbz+4M6RfRjENlWj4o1XCwNpajjXK/Qh6rLIZjUvPDNbHsKw/nZJOWOwsqWJ7gSYX5GAqnCbuJH1wo" +
            "H8vPO0P40w61hOw8/rVPx6Omafx1VQhrIvWIEB/Ps3qbOOFGnzevHZtPpycthqU1uTRLT9aI5lRnppMTq0MbGItWFU+0ewjhju6EWhuzpbuEUyur+GBOKo07" +
            "/0zeik/IWP6QiN5zuFbvwq5kI9MSZwuHzxXu7sFE28Vw72Lec89nfEgDCrt00fAgZuQukFz3K35zzqCYUczErP1EnfgX4dID6pNfMb11t3B9GoPNG7CKOoy6" +
            "4Wv0cx8zqfgJNi0vmJxxnemi+cZJn2CeLrqf8xkWogVGukdM134sue8lptqXWCW/ZLL6Ko7pt9D1PZeZX8VQr1y8KhehalhPaMd2fGuW4lXcT0zLWpwTO3h3" +
            "upbpjg0kpBwiK+Mi4X67ifffT3rIUTKC95MWsIumpNP0pZ2jKXIH3Qm7yPPsotSvhcWFS2hLKEM5bgr1gV4crstmb1YIh7MCOJHlx+OWJFb5G1A//S02RLjy" +
            "akk7+zJEK3QqTpYncKpay8GSKHaJHmzLVbFfan2sTsPh6gi5H8LByjCO1UZxtE6OawO52BMvX6+i1kJBmWTBJjuFMIiCz9Y58q+TAXAmgm+XenAuX48/r4zg" +
            "xeJgyoQfi+wVpJuPImHGZFRjhlIfo2Zeef7Aaxop4eHEqUOJC44k2j+UhKAQojzcyQzypizKm78+OM7KWh0N238mde5DojpO41a4STLXKiaE9DAtrA+jcLlV" +
            "S72nqHnHs2iAFUbq5qPwrSVz92eUnfqBt5NniaZnovCeS8Fe8Fj/HUPrT5D+5m8yd51ntHI+qrxrUpvPsCn6EsPSbzCs+BrL6pcYZt7FLO0N2wvnpQrnpz3G" +
            "KuMpttmfYZ7xEgPdJ0wXLzDRPUFZ/Fw04CoG6oUYxnThWbccv861eNStxKVsEerGVYTWLkM/sIRh1hk4hS8lXvgiKPQowcpDJAafEr8/SlH0WZKVeyiOPEO6" +
            "cifVUQcpD9pMgeti2tTr6REeWJ29lT+f/5GWyCJ8RkwgeYY+O3JiOVuRwMlsFadS3PikScN9qeNCZz1qpr3D455yTgjnb03y5UBhCHsylJyujOR4WZjohNzW" +
            "CBfUqDnREM7ZpijONEZxsjGCk61hnG5Tc7dPw4M+yYtRJpQaK+gPVvBinS8/7vLg172ecEjDmZwhrA2Qvphtxb+OZbIjbzr1ru9R7jCGTrUzar0hBE0fT364" +
            "ZIBI8f/ICEJ8lYT6BRCjCiZWqSLCxY4Q4cAza+dxc5vo4sJXRLTdxrf4CBYJ69ALXcJIv3lMD1vNSLcODMLn4FS6lfQPXjCmajMTug9RePe/JK19gsIkC8Wb" +
            "v3ffdJLKyzCj9gqKmPUoQlbytnoNZhln8Sx6iEfhc8wyH2Nd/Bqbyq8xK/5cNOZrTCXDmWQ+wzb/xcDrPOYpDyXrP8Y84TG2OV+KBj1iRuoD6cv7eGQcZbx3" +
            "B/bZywns24dx43rserbj0bSOsNaNuOVKX+pHM96lmoj8g7iHbiUo6hhq9Sl5Dk4S6naIeO/jZAWdJcPvGGneB8hVHqRQJV7gtYWmoD20+m2h3WclbZ5zqHVq" +
            "YFZIG3OjJTtMdSBm5HgWqf24WJHKJcnzt4uCuV/ox4vqEI5EmtBo/Ba705Xc7szjQG4Q22KtuVQawLmiQE6XBHK0TMm5llBZ4Ryu9ON0vZrLbXEcr47kcnsi" +
            "19qlDypc+XxpEtca/ej1GcKV9gDu9Xlxu20GnIiE/d486x7N2lAFxwon8H/7c3i9IoA+LwV7ctxER3IpURrhZziEMFtDUiXnRfgHEOQbTIwylhivCKK9vElQ" +
            "uZMfquLBoZ3EzP4Y1/JTovFbRGs3DdR/XOA8opuuENF4gcj+2+Qd/AGftQ/x2fmUxNu/M759HwqbXEZGzydm8ytyz/1v4PO3b967d+9+yqjYg9gK09tnS1bL" +
            "FI7LfsX0jOcY57/CRDTeOP0T7LI/F2/5CIeS7zDL+Vxm/9M/dED3EfrR4gsJ17DPu4N71W3GRK1kdPQ8HOt34i551WnWXibULMeyfQvutav/uJ54aszA30KM" +
            "Kz+DpXIVyvADBKiP4eX6Aelx9yhKeCT1P4nGaR+ZvsfJDzhBvt8hCvz2UeCxnUqv7TR4bqbDez2z/VYzy3c+s/z76AloYGVyK71BOuJHjqPKeLLocCyPahK4" +
            "nuHKp6VuvKzx4Vq+O332Q5jtMZGTReE8aE1lZ5wdp7N9uVIeJNyg5lJdIKeqVFxoCeNGj3BBZQDX21K51iJc0RrE1WYPrjS78nR+DA9n6digMaFDvOBqlRWn" +
            "c94S7TeHG4k87zdiqb+CtREKvlnqItqgZXvieGHSAP50cjYLi/xJ9JiGr9VUMjQaovyjCPXUEOwiLOSuIsxVsoevJ3sW9ouGnmNyqjyHqZuYmroOy/SVdBx4" +
            "xTNgjeSC6O0v8d3+iindZwk/8CVW/UdQBJTxXvpiptV8QMVtULjVoAiexfiMPcJz17AsfIp59gsci37AIvtLqe/XGOV9h0nhn4X1RfuzXuFZ8BlWuge4l/8d" +
            "x9J/M037JUbpXzJZNEA/9RJqyXVuudt5P6iOkVmzMVx2huELjjN6zjH0unbhOmcbLvXLGOSSh8I0C23TGRZ88HcyGu7hGbYHF9+duLrvJMTvBMkR10gLvUyc" +
            "52E0znvQuuxC57qDVPdtZHnvINttEwXOa6l0XSNzv5h2z8XMDhIGkFw4O6yDmWGVLI4rZlViBsXTp1A2eShHEn25XajiTp4Zt7Om8araje+Epw9Em9FqPkyy" +
            "gE54MJfdCQ5czHHneq4PN4pVXKpUcbbWn8udEVzr0XBZvOBeVwxXmty526/kQrdSOMGFR33RfCI9cjLdmmslrtxt8+JE8VROFw2Hw9J/3VYs9lOwP+FtrlVO" +
            "4/8OpnJ9ljsP1yTx+ngHxzZWcnBzL+XJSSQGxZEWm0lydIawYAyx7gEk+CnJjlJjWbYFB2E+g9QluIm+H/kzPJHa7/sGas6/xHnjLZTHvmZc23HCdn5B+YVf" +
            "GRTXiyK2j+G5q+n7FN5NXMUQzVosS6/gVPYJ1rkvsMj4GpO0b5iR9iUGKa/E67/BIPtbpsn9Nz3hWvASM91DDGJfS+77VbziBybEimbUfoJr0w2Gh3XxfkQt" +
            "Dr3bcN14lmGLDzNo4XGGdB/AbslZ9NI6UEwLQj+4l7DiE5j4LSYyT/JH6R2M7VfgK6zn5rxdmP8APrZb8TZbR7jTdnTe+0hw2Ua80wayxf91LutJdV1Lnttq" +
            "6v02Ueu+hFbvRaxM3Mp8zWJa/OrpCq2kLzyXORFJbEpOZqa7LVrJ4Mu9p3K7xpdXnQF8WeXCZ3lWcuvDw2IlfbZjme9lwn1hxRd9WbxoTeKr3nRe90ud6oI5" +
            "XqnkfKOa8+IFNxr8udMbyN15QVzpD+Zan5oPu4K5W+/D65nRfNgUwK5UI+YHvEOvu4KNMW/DxTq6hAEPxY/koG4EnMviVKsBj1aH8XRvMZf31sH/XsA/f2Hz" +
            "vMWkxmnRRWklF6YQ5qgk0V9FYUKUzOtGqddyerff5KXU/Y6snocvaLnxEu3hO/icvI3Ztou8X7kN666rtEtWfDtqCQrPKsYlzsW8/AMGhazApeojXITRjOIf" +
            "YJ/1BfZSb9PEV5gmfcO0BKl/8ufSY58yNe0JJgWvMS74XnpBvi7nZ6xSXmOW+qGw/FPs8+XxQpvRq12O1YYzTFh1miFzDqM/5yQO887g0XMUhUMuCscS7NM2" +
            "Yxe5FeuAzdiopI/9tmPpshFjy1VYW6/D01X8wnYjatddhErNg+3WEma3hhiH1SQ4ryPFawPxzivR2Mwn3mo2hW5LyLbuJtuimWa/OdT7dlDjXUODfym1Pqn0" +
            "R2czOySKzclaNsQEUzl9FKnjFML+Hjyt9uanWid+yJrCb2UmfF9hzXHNJNosRQsKYrjVnMKVijDuiBd8WBPG3YYo0fUgPuuN59XCZC62hXKw2oOLrT7cm+XP" +
            "49nCFx3e3G/34Fm/irvtrtzp9CFHX8Ht7hheLcqgXu5vDRrDsbRpA68ZHKgYweWFrny0O4s7e5u5eWghf3stFf3PT7x8dJ2m0nzyEnRkRkSRrFaRovYlft4F" +
            "Dkjhn/wV7v/0Dza/eMm+X3/jze5FUyp7SX30CrONJxlRuJ7x2XtYJPNu/uYz2JYFovv12BSfxrn8AY4FT7FI+Rjb5BfY6l5hm/Ql1lJ7i6TvJct9IXz3HH3x" +
            "dqOMx1L/z5mcIzkg/zuMpB+MdJdRtojvp22TXmrDtmcPlhsvMmL5EUavv4Th2mvY9R3HMG2p5MlUZgTPwTVpL6ZSd4dA4QFhOHvvXUy3XoW57QZc3HbgZLcB" +
            "T8fN+DhsJMp3N9HeO6X+Kwm0XCj8u4gI20VE2y0iV72NRGF/rV0/eR6LyHOeSa5dG8VurRQ41VDuU0uuUyYtoaV0R0i+9otipTaZZVER3OxuZoGwdvRgBeuD" +
            "DHhd78evVTb8XjKV3yr1+KZMn2u5dsx0HM7aSDM+6knnQn4gBzR23KsQRqhWc73UneezNFxqCuXlmiJ+2JjPuRpXbrYr+XBWgNy68bDPk0e90gdz1Zyu9eNs" +
            "QyTN1u/SZvEOK/3Gc67QgX8dzGBd6jscaLfj0e4CLq+v4rs7e/nqo2O8/vgk/Pcr/vXDx+xetYDanBQK40NJU3ty/Yc/Prtz5ic48tUv3Pnld76Q40//C+6V" +
            "HfhvOsug4hUMF40fq1lH6MIn6GXvQKFehEvjA8len+Gc/xoLqaO1cJ1j2iusNS8wj3iJTcJ3GMS8YGrsMwziHzNFcw+zlCcD/KeX8gzr2i/RbfwrLi2neDd6" +
            "JsMzl+Gw+i7W254xdvktJq28gZn4vnnnDt4LqZUcGodTzAbUiSewdd6Bm8dB7Fz3MHXGcuxlxsOjLmBtux4jwwV4Om/CesYC3CyX4m42Hw/j2Sgt5hLusEQ0" +
            "YAHhtnOFBRYRZj8bf9MWQkybibFqIcWhjVzXdrKF/3NdpfZu5TSGNZHllEyZdzKt6hSa/SOpdvHkXGszBwtThRXtyJn8PtVGQ7iU7co39a78VjuVX4rf5f9q" +
            "xvFttRGb/IbT7z6eIwXR3KgXLcjx4WK6HXdqlVyqcOOG6Me3y7J50pvIzZZovlgjGt4SxIP+MG53+QgfOPLx7CBut/tJX8SyMtaCcou3yB6v4GFvDM8WRDIn" +
            "/D1WFztye3MdG2vjObGiYcADvnp2mC8/3AF/vibHX/D64Wk6imIp1njx8T/e7CH6lF3PfuTRmz3YpO6v/vHfgT0YT77+GzWXJYfV7ODdYMlX8twM0azGe45k" +
            "ueYHWNV8gkmG1DrpC1nC9XEyyxEf45D4DXZSe/3gTzCMfM6M+BeYJEveT3iCjbC+Te4nmGXdI3COaEu78GRcI1M69+C15xnT1nzI5LWPMV3/FKultzGo3Cg5" +
            "U8MQVS3uWVuxUK3F3n07jra7sbPYiYnJesws1+P8Ruvdd2BtuRJL4wU4WS/FwWwO7pbz8LSag4dJL16mPQRYz8LfohelSSf+5l0EWncSZN2GxqWbMMs66YEq" +
            "UpwbiLMqJtWpghSnQjnOocC3kFK/HOmJGOYkldIaEs+KlAy6fVxZqQmS2YujwsYA7TDJYrFGfFlty9+qpvHPiqH8pWgo39TacjHDml6HwWzVWPGiM5ELmc7c" +
            "qPLnZrVksSpf7lco+ag9gfudSZyvCxMf0HKjM4yP5sZwt1vFvQ5hi+43GTOAj5Zlcak3gV7/ETxdlsG+EjcafEayULzo7qYe2uO9eXpkNfz2mP/7+hz//fYM" +
            "94/N48j6evj1Eb9/doGNvQVc/TssPXePgORSvpBeuPf3//HwP3/sw3tVPGHt58jsC++51gvDH8F/5Q94LvmRibnXsav5AtM80XHdl8yI+RyL2C/QD3mOQeSb" +
            "1+y+YazfPZR5v+KQ/ANmST9ioPuaKeIR3g0vCKm5zLsBzSjKF2Ow6yYWJ75l7KZn6K15hJPcOvUcZ3h4OwqzzIG9Ysu2/gm94FVMdFmKnc8OjK3WY2Ur/u+w" +
            "CWOTxZibLsZ4+hxMDGdhaz4PW7NZ2Em9nc16cDXvxNNMlmkH3iYd+Ji04Wvajp+5LIsm0YVaQu0aibKvJ8SilFDTfOLsSkl2qyDeLo80jyLSPXJJc0ulxD+T" +
            "HA8N5f5Sb58QZkaE0yl5ek64JxsyQ5kZakPSBAWdduN4VBHCL40e/FI+lX/XTeHvtZN4Ifd3Br/FfI/3udeWzLOOLK7kic5ne/KgzE+O40QfQrnUECI+H8uV" +
            "llCZ/wjuSR/caw/ggTDA7S5vLvf5c3V+mPBePGe71FR5jhV2GUVPpBNHekp5fXq3zPsn8Kc7/Hx/L/z1I5KEVze0xfPvTw7wj6dH4asrXP03bLj5jHdGmbJi" +
            "5xnO/wJbv/mdvZIDlnzyO7M/+afk7D2ErHpCxqH/YD/rKyw7v8amTrJa6mOmSZ7Tl3l3SPoLZsEvsQh/haPue0ziXxLYCIbRwnzhTzGIeoZV1ud4NrwWBjzE" +
            "e2GzsO47hPmBB4z54D7Dt33ElC2f4LbtBcb1B1G4lDPCrQFL8RzX9GPkL/iJkS5rMPTawjjLRRg7yX2LRRiZzMPEeA621tIDxn2YGnZib/Wm9l2Y69dhN6MJ" +
            "hxmNOBk34TKjAVejBjzk2MukGW/TJnzM6+R+Gb5mJQRalaK2KiTMpoAIm3wirLLROOQQY5tJknvOwPXAMTaRZHpryZQ8XROaSKUyiK6IEPEEJ1oC7ViaHMys" +
            "CE+KTCaTJlpwKs6Gz/LN+blsPL9VDeP3lin80GLBqYwZtBorOKPz4nVTJlfTvLme482tchUfSy681RrCg55ons5N5G5HKDeb/EQX/Lnf48f1Tk8ud3tyda4v" +
            "1xb48nBDKl8emMPJ3nJUQ958lqgSvhE1//o+/3lwjH/ePcy1DfOZLpnl+poG/iNe8N35Vfx6dzfnfod1t5/jGJCGbUgOzecfsur/oP7L38l68hPZT34l+c7v" +
            "hJ/8G65rvsG5/wcs67/CQDKefdXfmJ7xE/rx3zE98DNcw78SDvsQj9DnaKT21vk/MUr3EoOcl/iVPkVdeo2J6oUM0y3DYtfnTD35PWMOf8HEA8+x3fMhnmuu" +
            "MjqmD4VFIfrR67BOPsxE362Y+x/CQXmKKWabmWy/ibGi7dMcFzLVuhdjiz4M9FuxMOnBQmpuYtiMqdTY3FBqL/W1NqzGdnoV9tOrcTT8YzlPr8HFqFaYoAZv" +
            "80rpgTLJh4X4mOXhb5lLsE0uITbZ0gtSb8dcQi10xDploHPPIMkzlVTfZMkOccTaq8j3CqLAw5vG6HCao4IodrZkXoQfu7MjWBFmT9pwBQvdxvK0yo7vmsz4" +
            "pnSCaIIRf2o05/sGd/YIG25znMiz2mTuVUZzOc+Dczm23K7z5UadF+fK7LnZ4MpjYYC7HV5caPHmUqcfd7p9udLhzMV+J+6uiuR0XxI/n1rDJ5vb+WxHN/+7" +
            "v48vDy/jz8c38+T/WTrv+KbK9/0HEBmF7j3SNKsjaZq2Sffee++9d2lLd9l7772nAxBEFBVERFFRHCwRRUFFBQQFZbj9vH8P/f7+eF5ZPW1eue77uq93z8k5" +
            "W1dQo/WiUCXl7SXdIugf4K8zu/nfx8+JPv+bhe9dEdzWQuqcrTg2Tafx/Pfkff0zMZduk/jRb8ScfIDumVsii/2Idv4tPPp+xK35Bi5V32ObJdgu4wY+aTfx" +
            "ibxKYPQVjPGf4xB9BrPUM5iXnUXadAZF2T7Gx83Bd+AQmcfuYv/6HSa+eQ/nIz/g8cxlFLMPiZ6vR+LeQFDx8+jzjuIQug97v30ojC/h7LYDtdczOLpvwkm7" +
            "Fjv1HFw9Z6MSy9m5B5V8iliDyKXdKFzFknbipejGy60Tb3knOnHr4zYJnawdvWs7vrJJ+Lu1EujWTKRXO5GaFoJU5SInFBOmKibGq0RkxUpi1AWk6srI8i0l" +
            "RZNHmm8O/g4BzKifzlDlZOELMZQYwyg3htMVlywYIZX5iREMBbuwIMaNI+1ZLI6QUifq4I0CKY+mR/B9vRO/dDjxsMed3/tjOJ0mZ6XI8m+UBwomiOdkjS/v" +
            "NBiFFwRxdUYCHwiufF/kh7OCAU9NCxY1EMwHQyHicSBnFgbx/rIormyu5eNlNXy5pYefDy3g3kuz+W7PED/t30h/gJ5dVSVMjzBwqKsC3tvHXye3wvn97LwP" +
            "RS+eQtLSR/Ol7xhT2o1uuWClKzfxP3eTsJP38X/+Lrpdv6Hf/gfOM29g330dt547mOd/hmfFDVQpV1BHXsE94iq+GXfQ5d7ANesMeqF7SO8bmGQtYHTpUsKe" +
            "F31+9CfG7LqM9TGRDw9/ReTeT7GvWYXEqwHrjI0ENr+DR/pRtNGvo9KLvg8W+d/vECrDc9i4LUMhX4zC+YnPL0KvWYVaPROZTPS7mOVK4e9KWQ8a9ymoRX/L" +
            "nVrxlHWgEVpr3drxFnprZU2iFprxkTdhUDTj61wt+r5R+EAdAYoSkRGKxDzIFTWQS7ymiAhFFvFe+aT6FJGkFT0fVESUZzwbRK9sXriRSK0f5ZGJ1AelMSk4" +
            "nf6IRKbEhDI1zps3Z5fw6MU+flhbye54NVNtJewy2PBTbxwPerxEHnDldrsrP/dpuTbJnX0xY9gZ78DVmeVc6sjkzQJ/3qsy8KFgxDODobwzaODtXk/eHxDc" +
            "MBjCmX6RBeY8+T+SL58tih9mx683dAp26OLG+iK+WVvO9sJ4pgfpuL1tJj2a8RxqK+DRq7v4783N8OkzzLr2iJqPriNp7qbw+g+kHv8IScVk9K9/SuSnv6I/" +
            "8jORAh+9tj7GdvaP2E77AYvJ17Bq/BK3hm+QCa7zLf4GbeY1fPJE/ku6hFOayH29X+Pd9obgOtGn0/YQdep73I5dZ+zBr9B+/D/83/gF5fJ3kAR2DX/PS5Uv" +
            "+jzvZcaH7sUx8ghyw2FkHs+ItRcn9WacfdZj7TYLJ7sBoeMc1A6zCfcTLBK0BqnrZFxculGpB1ErB4b7XymWv07MA5dWMQva8JI1i9Uo6qBB+EEDemUDBlUD" +
            "fi5VIg/UYpCVEqgsJU5fLTQtJlCeLmogk1S/MqJUGSRqcgUr5glfSCHMI1YwRTQGdSBR/iGkBoRTEZjM5MgsWgUX9oQaGAj15KWOdD5bkMvrVXo2B7nyTKSe" +
            "flMJi1wkXK724O5kTx4MqrnT48zDme5cmyxnf4opc90lnKmN5YtOsW22nnfrg4cZ8US7F+92qfhwsmaYF8/0xPBBfyQXZ8VwbVEsn8+O4/uVDXw5v4yrc1O5" +
            "PDOZdqmEc3NquTwvh3lBT/FCQxz3X1gF7+6Cc/vpufY3ee9+jaS8hdzr35Hw001M9u5Hkj2Z/DcfoD38K057fkOx5jGyuQ+xHLiJrcj9ro2f41L0Kaqya6jL" +
            "ryMtuoRpynEC2i8S2XeJsQlbkKRvQrn5C7TvPsbs9E1Mz9zE+5P7wu8vYFm9SfR8D2NDtuBX8ynqvNPYx76MOvVNFHFHcRHe72jcLub9WqT61Th5zMdTvwRH" +
            "p1702jm42LTir5lFWPBKnFwm4+gs9Ff93wxwFfeVsl5CjItwF7PfXXi+p1sLnkJ3L7c6NPJqvBVV+CmrMcqqxQyoxtelaFj/BEMdEaLvA9xSiPLKJkKVTohr" +
            "0rD+qb55RGtTSRQMGOodR3JYBhH6SPJiU8kwBolZ4EdHpJEZkf70qZ34oK2SXVF69sXrOJSs4VRZCC9luLPez5RuMwmvprryQ4eWewNu3Oyx4NECOT/PcedC" +
            "u5x1hjHsSxG+MK2OM82JHM524o0yZy70+nG6Rc9rlWJW9OXwnZj7383K4N1Gnbifyr1lRbxbG8DVqSUs9xsnak/GD6viubUpm1lBEg49+Y7y8wP8eWQ5nD1A" +
            "w4+Q8+ltJLV95H59E+X3XxP820MkrSuw7t1P4InHWG79Aefl95DNf4Rl7x0s2r7Bvv4C8gbR66Wfiwx4Gc/mswQMfYRd3QtIklfgOHCSxJOgevt/OIl+txW6" +
            "S98TuWH3WcH784X2bQQW7SOw9n3Bd69jHf8CljF7sY7cjoXoaeug5SjjNiEPEVlPOxdX5UycZSLbCc1dRL8rnTvQivzm7FiPQiXmu+90wQJduLk24acfxEfT" +
            "j5u0XnBgCz7C330F0/koqtHKK4UPlAkfKBVZoBS9WwlGRbmYA4UYXIsIE9r7yRPxl0aSZsijIKySLL8CEpSJxKrjSQ3MIkxoH+2fRqQ+nhRjEulB0eSEB1ES" +
            "5kuR1pWFKZEsDvTlmdgYDsbHMV9qxeF0HV9NyWVnrCNHiwI4mBHENEcJGzyFF9S6iP734Zs2M273TOTBXAU3hvw5mGzHAtVIPmxK5HJ3HK/mSDlRLOej5iBO" +
            "10fydn0EX/ancKE1hM9agrgmMuOHVZ7cmJrG3nhPNoeY83BlKHeXabi9TswlrYQj1a482NMCx4UHfHwAw8Vf8Hz1ApKqaRR8dhfVrd/wevQ/Ii7dRVI5C8ct" +
            "76HdLzx/yRc4LryD8+ADLJp/wG7wOuaTRN92C+9v+YSMGW9jmS2ye9Fi5Ae+IfAzsDn9J+Zn/8Hp/QcEHb8qttkp/L4fk6jlhLa+hjJrA2MMvf93HEFIH5KI" +
            "frEmMSq0DYm8mAkqwQHu0/E1rEIpWN7euR93/0U4ygbwdB3A3b4Za7NkpLJs7KT5mNul4+wUj6V5EK4umZiZhGNvEYrMOgx3i0g8bRIEBxSiFznPT9TAk/sa" +
            "VTY+HnmEKWsIkVXi65YhaiICT0slQS5aWtLqKDRmkeYWSYZ7DDH6KMJE7o/yThackEy6NpJCQ6j4GX8qQwyCBwxUaz2odHCgwmQMvdYmzHAeR6/QutdVQoud" +
            "hCZLCWuMzhzJMLDWewILnSWcTJJzrzuIv4dc+XuKOfe7xvP3bA3fdQaw3udptsUpuTxUykflQbyVIedMpZZTlV6cLFFztSOUy+UenCtw48v2ME5XG5gq2PLG" +
            "vBTuzZbxaKGCB2uymSPmysu5Dvy+s5G/ji3hv7M7kJ2/jeqUmP+ZvQQc+QyvW3/j/POf+N78A4dXRK9WzST8yHVs1l9h4qLvGN38FbIpv+IwTWSA7rPoZ3xN" +
            "1KLPkcR3IZ+3F+N7d5CfE9te+hfrj/5AfuYPfF69iaRE9LxvI4Hlh4lpFHPfewhJ+BQsa9bgv/Algla+TvDq4wSte43QVa8QOucAsvxFmGhFPrAoQ66fgoum" +
            "B5lhGrbySXi4deBsVUBO4SzSy2cQWTCTqIJpZIr8mpjZQFreAOm5/aRkNBIbWUCUT6Zgv3hcbGNEdszE6FEu+DBPzIxEdF6ZRIv8F6OuFb1dJHwhmOrELCoi" +
            "4/AzlxLnqKdUI3pdFiBmv2HY85P9MoT2yVQb46j1C6BG70O2qy35ThPp9nFkc4KWj9vT+WJSAte6YrnYHMyVrigudERxrEDL7iBzptlI2BRgy6E4LXPHS3he" +
            "PYFfO32532HNv9Msudc1hp8mS/lBcOLhTDlr/c25WB8n/CKSE5lK3spTc74xkLcyXfksx43PCtScE/w4Ryk8ZSCUn5eEwFoV9+c68+20OGaJ+nu/XM/9NTU8" +
            "eHUufL0P6aVfcXzzOrZr3hJaL8Dw7SPc7j5m4q1f8X8Io2dtYlT7WuQv3WX0xh9Rbvsb64GrKAYuk7D6Jq51L4qeX4XlzndRXfoZl6t/MvLUt6KuHqN76y6e" +
            "C95E4tMu6mMxXrWvEFTwOiPVcxmRtQavnRdxEfwvf/MuLnsvId99Abc9l7Hf+jGKZy/ivu09vOYfwK50uvCDBEx9izHzqUAV2k5QSAvmNuHEVE4joXslkV3r" +
            "ie1aQ/KkmSQ3DRFfM5PstsVk1Q9Q2jRAVdUAlZVTyCkS3uGXi7VdCG5usfj6pqP3TMXonEu4mAlRugLcbbzJD4lk78yZDKTnEWxiT6SpgnxNNLHeIu8FJxLq" +
            "aqREH0e1px9tWi1ZZuMY8Lbn3aZIbvQF8bBPzePJ9jzutOLvHhset5nye5sFf7Tb8Gubo8j9nnxer2Gj30imW0h4KzSQ52UyFov77+Wo+H2Onp8GTPlligl/" +
            "z3fl9xkaLlQLTvR+imP54VyuK+BUagivxio5k+nF+VQPbjbGsT3Mjh1xE/l7VQCPFtlxf5k5vyxzZ67wg12hKi43ZvDLilb48TXuv78S29N3Ub53H+Obv4iZ" +
            "vx7JgmeI+g1G3fgJp/sPib18Qzw/nzHL3sF19x2e7hM9v/onPGacQ1KwjaebdxJw4gbu3/2O5XcPMf3oNj5f/ovunTtI2ncLph9AlbkFVdUh/DvfRqKagXXR" +
            "AYIP38HhpW8Z/dL3jHnxW6x2XcJ5+3kcd15i/PozSNaeZOyWE9is2U/YbpEPKgaRyKJximrB1D0LV20m411C8RO6Rkxdh6FnA/r2pQQ2DJDQOZPQSuEJVTOI" +
            "rZpMYkkzeaWThFe0k17cQWXrLArLu/HyisHJ1pswXeYw84UrBPPpC1FOUFIRFcP69kbW11axq6WDVCdPoh29yQtOIUTuR44+glKtgQJHBwpNR/N8QTTfTi3i" +
            "dqc/v3e48LhlNI9bJdxrH8PPk8Zzv11o0mUFXRb8N8mUv0RtPOhVcHeqkTPFHqx2GMt+4SEvhkYw22okB8JH8u1kJ+7PcOLHzqf5o9+Sx1NU3JwZxbYwC/aF" +
            "KrneWsynhbEcCXLjeLCCs9l+LBJ54tbScB4vtOKPxWN4sEHJkcKnWC484WxJAp9UxfJ4YwecWswHm8swf+EWyreEX7/wDe6HxBxInYnq2U8xPAKT775Bc+8O" +
            "0pMfC60XUvzcfdI23UUx9CGS+r2YrXqHsO/B+evfkFy4hIPgx9Bzv2C3+DUkcVOQJC3DveYE7uVH8W07gkTfiiRtEdHv/MOYl39k4ju/YS+ygtezV4l64Zqo" +
            "q7fQrzlB6HMf4bLtOLbbX8d8wyuMnraT8I1ncCxegcQ6DSvh3U+umSlxisZf1Gbw1A14tq8WvLmCsJZZZPUtIaVtFSlNSymaNI+C2h7S0mvIyayjtKqbnPwG" +
            "sjPLaK/vIV5wu5OJWnBfPmHqdBJEXeks3GlLSmJVdT5bKzJ5trGCZWU1+Jm4kKaNJlMXKXg/nExHa/Idx/NqawbXe9K41ezDg1rRc+VjedBpzw3h3ddnhXJu" +
            "WjSf9Anv7wrkRqcnD7uf7BcaLfxAwt32cdydruG4mOWz3cezx9+HDzNT2CAbySrBiZ9UuIsaCOSPIRf+nmbF7e7xYp578065A6u9R/BJeTIXy/O41ljKOpWE" +
            "TzvFfN8k4+5sCY+WjedTUWNDIl+cz/Xnm8owztf7cHFKAB/ODebMojBsn/kNq+d/xuKQ4POXBdstED1avAj/z26j+/VXrH7+AeWPd7BZdlg8v5wJrQcZ2f0i" +
            "/iduEvrt/5jwqejj7//A994jXN78kPHTnxH5X3B3/TNoGoTmQZuEbq8iLRY8GD0Z/yPfiHlzm3EfPEYi+j740A94zXhV5L5ukQ2bRI2UiG3KkE7bhnrDUVx3" +
            "nsJ69RvYzX5dZI3jSNTVuIf14uTbwgjRr56ix306l6NpW0tIz2b0Jd1Iw4rwjG7HJ66D4IQGElPraBT+0VEzlYLsevLSy6nKLac0OZ8pDf1C93DcJnoS451G" +
            "suA7g4WKurAQnums4rnKRPZWpTOQkEiMs4ZsXQKVhsThY4JLHEdwpCmWD0Xm+r7Nj2+LxWff6sFfAwEcT7dkZfBT1IrPvtRBQrW47XKTsD3Sgs8aldwX/f+o" +
            "R+T9roncm+/NzXV57Mp0p2uUyIYOtrwaY+TtjFCWim0PB5vysMeXf6cq+HOmJT/2P83PCz34bqqBDV4jOJFmEH6hZFfASP5coebu3BE8WmXOz8vcmCNqYpu/" +
            "CR8nenA1z4fztR68WmHLx9MNnJ7iI3rsd0y23WH8i7cYte9LnERfSvqFhv1rSbj+M/Y//4L0v7/xviF4YMFmxhw+h+baX7heu4vrN/fwuwv6Tx8iXys0LJwm" +
            "cuQ8dDNOYpawAIn/VJHx1qHqeFdkgDZMqlejO3GHp176mlEvXkN3/BdsajYiscvGJGEmYyKHGOXXgsWTc0OI/C5J6MZl3gs4rjqG1aJj6JafwqfyyTmIUnAP" +
            "EPPAPANjzRLCe9cT3r2DpP5duMfVIRnjiYVLNpZ2Gdg7xuJsHYyHWAm6LOpz22kpbqMpvYyO7PLh78Us6pyDm6mcSM8oioNyRZ9L6U1OYEVBDNsyfDjdX87s" +
            "7DS8njKjWJdMZ3gGgRIJB4p0XOsTfdXiwtVyR+71hnGxKYpB+RjyJkqoU9nSobFnbrCa7anBLPKTUmcqoVGstcETREYL58cF0VwQ2i7QSJgp8tnrKf4ssDNh" +
            "lpMdi92cOB6m5jnX0TwjHc8XJX5iXnjx6wJnbvebiWzgyD/LgzldaskmXwn/Lo3hv2nmPBiawE8rYnku25YVoubOpNrxgcgJH6XqOJWt5p1qHcdrdYIFvRi/" +
            "Sui6/FvGPHsLyZ4rSJ6/jM3zl5BkDA4fg6O78xjTH2+hefAYy8tfobr3J7Lbj7H64lt03z9Ce/Y+JgtfFzp3IqvfjnLKSdHLs7Gp3kbiso+QRC7GqXK/4LtB" +
            "jCvfQ/32b5i98RNakQ0tpx5CoqhBHj0Xi4gZ2MfPxzZwCLV4HFm2XdRFJrKOjciWHMR06SvYzDtI5NBuRkrTcfPpxMSlnOD6pQRNWomuVnh/41r0ic1C+1ii" +
            "RT35iNzpoSwkwKsIozQNpYkRrUMwhbFldBU00pRUSG9+HU1pJbQU1OA61l5wXDrBpm60CJ7f05zLoYpgni8LY0ZmPAGmdrSGF5Jlq6RPa8Vl4enfNAjtS8z4" +
            "WWS+t4q8qRDaFtpb0BeXSMuT/UKhIfT4eLAzJ4kzvS2caKtmaYQfjWJu9HqYcqjMwBQxm6fLJOwIkfJqaiALXMx4tyZfMIIHy8wlHPN14AWVK+utnxKeYMed" +
            "QU9+nyYXmVBkg76JPFyk4o7whl+H5DDdBWb58b7ghF7BF29EmvBO+GjeSVDyQX4IH5YH8lKGkleKfHi5zJcRS79jxHIx95d/iWT9RUbsPIv6pat4zD8hZvUM" +
            "4i/+QvhPf6B50uv3/sb99n10N+8Ref0hnoIXJRWrkKTMQzv7TezbDyIJm4Pt/A9I/fBfjHvE6yGTURes5anIOUTvvIH9sV+xPfEzoS98waiUmYzSTUWVtBnz" +
            "kNmY+A9g7TsNa80UnH3nMlbdxoiAKvwW7hF88jIm85/DOHc3EwKLcfIswVSaib5iJmGDGzFO2kRMxyZ0CU2YOsYg1zTgpqrHw7MGT1UR3q55hHtX4CFLHL7W" +
            "RltJJ9XJxcP618VlMlTbTICrJxki48fYeVAXaGBnYy77ywJ4pSmBweRwouxc6QjNIXG0Ga/VxHK+yZfrtV5cqdRwrj1JaCoZPp9XZ6iRGj8/ynS+NASG0xQQ" +
            "TJNWw7yoYPYUZLA3L521sVFMdpNSPnYEMzzsOFoSyeGcYNYF+TJkbyJyWjC/DCbxfq47K+0lvOJnz2u+9qwV9fWS9xhuCHb4a5Y/f89TcbtnDCx05p+pTvw5" +
            "I4DH03NZajORF/QevBduw+loM15JUvJimpajOZ68VqDh5XwfDubqkcy/xJhVXyPdcw/zVRcJfOkGYc9fQT5wlDFVm0X2X0v4B7dwevUzbN+4gPOpj9G+cwGH" +
            "FS8jyZsnmHEbkduvIxs8KjxgCMPhX0n+AgI+BbsNwvdDWlEWLGFU3Fyi993G/q1H4vf8RNiOD8S8n4SpYRFOYZsY5z04fB5BB/107D2noNAtwMq9U/BCNn5T" +
            "N2G98hXGLt6P3+LdjA8vwtorm3GuSehrZxIyZROGjg3D56PzTW7FXBqPyrsVN3ULMmU1rrIitPIKfD0q8dWLbOESKR6HMkkwQbuYA905pQxUinwYHEmorcew" +
            "/hV+vuxoKWJrrh/7amKG+z/KypEmXRx1zjI+6UrhkxovrjWE8l13Dv1qC9LMR9MdJ7jA359ifQCrmieTrgmmNCSeyoAQ6gx6GjUKJnnKmRcexYqYJFZHJ7JR" +
            "1MLhojiOlMSxNTmaAZfxnK/25MHUAO5NjeB2bySbRG1dStZyMVrLc05PsVb09ntZTlzvVPBolprvq0dxvXQif84p4lCoJ8snmPKsXMrZFDG/0j15PsSVzb7W" +
            "PBdqx55Qa7ZGOPJchpYRS85isvIClsvOk3TgHsmz38MhbZnIalNQLT6FpEzM26JFSCqXIOlahyQgl6ebljBRaDo+fyPaTdd5uv8YE5t2IGnZjNe5fzF79yGa" +
            "D/7D45mvkER1YyHypCRtKuGC96xP38Py5E38tp4WWa8Re5EPXALXYyl63sV3AGePTuxcGlF5TRE9LvR3ScO3ZzvWy99mvOAK30W7GBdTwHiR0yXKJLyE/oED" +
            "6wVfrBX9vxF9chPmzqL/vZpwVjTiLK9C7VGLxr0OhUsxaq8S9IYSTJ5WUpAoGCC7kv6CcrryCujMLSRMaB/pIPQ3BrOxqYytZdEcmVzI5OgQslxVpEx0YXVc" +
            "HGcbwzlXrRUrglNVKWSbSCj2UAovSSbRO4RodTD5YcIrfOJJC0wiNSiC/KhwauJCaIkJojsmnMmB/swJC2KeUccyo7uoAx+Wx+poFpnvo3p37g1582iKBw+6" +
            "tbwbZc5F0cOviDx3Ll4metuWpbJRrAkYT/No4Qn6p9kovGGO8JMFFmYcC9az2PIpZsos2R7mwzuxIbwd6MWrPg4cDLBne7gzy0JtMZ/xGt5bP0O//RLjWnYw" +
            "LmshWesE7xUuQrPmfTQbLqNefR6/zeeY0LcTU+G/acevEbf9IpLEBUzofwuvdV9iXi/yfc4MbD56yMQr4HH+P1wF00vipmJSKGonc4DAl7/E4p27WJ+6jXGH" +
            "YPyAVuxj1mPtvxxTzyHsdYM4KTuwc20S/j2AjbqVEW5Z+Iu/a7P89LD+PnM3Mj4mm1GaDCQemaiF/gH9G4b1j520Eb/EBszswoT31+Aiqxe3dUJ/cetUiptL" +
            "CY4uOej9K5A5huGvDKWvrJmOLKF9VjZTyypJdjfia+ZKS3waGya1sL25kELZBHxF3msxhhMhGc/B8gK+6Ijhw1I159szWGCQUywyW73o70S1N1lBSaQZ0ghX" +
            "RQ7rn2yIFZwZSXJkBOkxYeQK/cuigwRjGBgMC2aZqIWV4UY2xAeyJsGPDpEDz3d588sMNY8GLfmnV8prupGci/Xg43An3g93oE+8ny860/luUaGondGC/SZy" +
            "sTiLw0GBHAr05XCgnCWuIznbny84xInV9qN4y1/JmRhf9ok8se7JdQsTZdTuvILfwAGh3RCS7pWEfPsrAVd+QpLVg2bBccHyn2C78FOh30JRCyfx+/oX3L+/" +
            "T8D1fxi5WPBYwVa0PSexqFiPpHEDbhdh1Me/4/ndf5jvECwZOwvz9KVi+ykYjlzF7ORdHE7+TNi2D4b1N4teyBjDAp7ymsF4sSxUU3DUThfzX/CA6snxINno" +
            "Bzdjteo4E5e+gGH2fKxEj0kEq0vUhbjXLhj+DmDQpM2kdG0lILEeC6tglIpy3KTVqES+dHUqQmFfIjy/GqVbER6qHHyUCeidjXQVNzApM5++nDyml1SQ6G5A" +
            "9bSN6ONsBgtK8B4jIUvpNJzlqtx1FIgZcKy+hEuNwbxXKOejngKaZOOpcBxDm15FoaeGPF0oKZ4R5PolkukZQM6TczAGhhAfHExoSBDhEcGkxgdTJby+JTCM" +
            "OVGJzAsLYKXIBxvCw+i2Fpm9WcEPQ9Y87pfAkBPvBDvwQUQYJ/VatliO5HiUHb/MjuDWfBU/rgpkT5GeSc5P81yQC8djFRyIcmaqi4Sv5wq2XGrgSIYls+0k" +
            "bPFz4ZX0EHZHSFnrOxZ9wRxMYptI2n6Mwi9v4vPTTdyvPtkf0IHvnDeEP7yP7dxz4rHI2OtOofnqDrZ37uH6M0jfF+yQuwKLPNF3fSIPFK/GRcz90Zf+xubT" +
            "G8gOfogkZgbmCUuHuTDkle8xfVMw5Zu/ELJJ9L+hlbEhs7AKX4Fj+Cqs9XMxUQ5g5jGEmc/U4Wu8StxS8e1eh+2y17FYeoigWQtwTspmpJvwf5nQv2YxgYNb" +
            "Rf7bQnL3NgKTGrCwNg7rLBd84OpUgFpWRrhvF2pHob2iGK06B3f7YAxSIy05lcL7i+lJzWZuaTUJHga0FjLiPYPQWTmTbTSQ7+9Lpc5AqVRNkaU1RyvSOVfp" +
            "y9eTQnmjJY0qVxOqpRPpNHhR42sgQeZNtDKIKFUQGaIWkr38SPMPJj1E9L/ggqyUFFLjwsgTM6UtJo6+iHAGgrSsiDayLy6JhY7jONco5cF8Bx4PSvitXWT4" +
            "EBkfxSay28mePW5j+GNaBL9MlXFzumCPFX4cn5RCp6MJp5K8OehnzpZgW1ptJXy/Ssd3yz24tSqJky3C77ws6fO0Y3uSDzsSPPCtHmDOu1/Q9sk1yq5cJ+vr" +
            "r8g8d5URBTMEtx8SHnAet2lnkbc9yXe9eB+7iPbru9hdf4z/HXB/co6HwqWoqp5Fkr0W58/B4pbIfu9/hc8h4Rtx05iYsIQRmauIfPkeE978CZtTdzFu+Xj4" +
            "+yNj9DNxC12JPnwJcm0PpvJmxirbGeXZw0T9ZMa7pRPevg7n+aewXHACv+nrkaXUCPbLZKy0EM+6pfiI/vdu20hcz1YC0xqxcgxEITK/q7QEZ6c8PNXlRAVM" +
            "xtU2E7ksG51nNmpbf/xd/GgT3NeZW0R3UiYLyupJ0Qbh56Qh2iMUb1s1ais7skR+L/MyUuUm9HVy4KTggrfSXLnyZN/LpCyq5RZUKqXUCu3TVWIGeIWRFp5F" +
            "qH8SEcGZBOtjSfKPI9k7mEwfA0UBgeQHB1GdEElrSiidCTqmxCtYHKFkv+DONUK3S0XjRf4z5/608fzS78rbEUL/xFBmj5dwq8ufhwOO/NwtXpsiH95HODBS" +
            "wk7x/t4P9uOYUc22cHd6PEfy4bw4vtuWx+3NZfywtY2zm6aIOk+m2lfB/JxEPFoXYt80B0m18P/2eYyePJ8RNbMZJXKApu841h2v49T/Dtqh9zCr3SN+biEx" +
            "5+6g/PIR9p/dJ/CLR1gtfkVoOZWJ5buRnvuTEZfuoL36AOmOkyIjiFyf+H/7e8Jeecj4E8I/3rlD6M6PGRPRL3L/FKw9p2Iva8XCsRIbj3as9D2M8pjEGM8G" +
            "7DzySGzfjPPc05jPPoV+YDuK5HaR8TNFreShb1om5tdmvFtXE9+7hZC8DmwVsag0pbjKRd5zL8fWNgVXuyzcZXko5Bmo5fHo3EIJ8Qylq7yJSVlFTM0uEp9L" +
            "LQleQXhYuFGbWsrcph7x2JMgW2uKxVwvl3tQ4mjP4co0rjaHc6HChw96cqhTWFClllOm8SVF6UuybyS+SiNGv1gSk8oJMaaR6JtEhncUZX5hNIRFURxopCzC" +
            "QFmYO5OTNSzK9WZRmJRnhX7bXMfybYUl/82w5+6QObcHlHyS4c0mp5HD/P9gtp5fe01gjgt/z4lku2Ik+1Vqke/ieNnTc5j7Ngdp6HIdx8o4GQtDzVji9zQL" +
            "A8yHzzs+lGakKdCDJOtxjKrdyQixJA17Gdm8l9H1uxhTuxubxsM4db+J1RTBcC1HMGt9BeXgCeHji7EYeo6kb/5F+tlPOH5zH78L93Co3sLEvNV4f/gAy8u/" +
            "4XLlZ+yee5tRiZOwT5uOJH8F3kfvM/HkA5zfukP45jeYEFGLiXcLNt79Que+4TVR2cl4VRumulZGK/Kx1+QTLXKd66LTTJx+nIC+Z4fPVW6qTMXcKx3/urlE" +
            "ivwf2bWaqNZlhJUOYeuViZNHPnbSHJEB85HLC/B0LxZ5IBWFIhm5WxQyqfDp8AwackT2zy1lMKeQmUL/GPcAVBOdRCYoZFVTA1sb88VnpifXzZFCd3dSnZzY" +
            "U5nD5eYYzpV58/HkdOaEyilxNqPGR0uexocsvxBivA1E+oQQJWrgyfVaYlSxIhPEkSceFwWFUxQutI/0oSbQnRmJASxJ1bMy0ovdgge2eNjwRbkDD3vshM42" +
            "/DFdsLqvCft0E7k7EAhLDNxsG8d/c3R8mKVihd3TPO+vZZdOwwuhRnaK+5v9vdhi9GS1hyPbdE5sU1uyTmHCtkApM9xGsyVGyTbh/xb9H2I18AH2g2dxenLb" +
            "/S42k07iMOltHLtPM6bpKOPaX8e07RWs219C3SfmQN5CbNcfI/S7Pxjz1U9IxSwI3f8Fo5q243b6Nxy+/B/Sr/7E/uBHmGT1YxHfi6R+G+oj9zE79juy4/eI" +
            "2PQ6NolNjNPWYKXpwtJ9QKwhJsp7mSAXDO9Vy9NuGbgYS4kQuU624n0s57+FvmMvfvkzeEoaiUNgNiHV04nvWk7q5FXk9qwjIKsbU7dknD3zhz3Aw6MMuTR7" +
            "eJ+/pzIdjSoZpWs4vt5xVBU2UZteQnf+k2uwFTEpNYdwuS+xIrM1xCSwtqaYHYL/DjQkC6+MI0XqRIKLG4vS4zlTFc5XTcFc6orjjfoY2twmUKNwFh7gQ6Lc" +
            "iwRv/2Hmi9IYBAOEkeWfSL5BLJ8ICsQMqAzypU0wQG9cJIPh/qyM0bIuSM0uo5E1MlOu1ih4PNWN33qteNAn5XScGd8K3vxrqpbvWybyz0IfvmhUsFH+FNu1" +
            "bhxNj2KtQc1ywZGrQ3RsCNWzKVDHMyF+7PXX8LL4G/uNSnb42PFsiAvXBvM50xjJ+Jo3hpdJrcjXDccZX/86Y6qOYFL+MuZlR7AtPyr4/RCmlYd4uvZ5nGYL" +
            "D2jagPWs/eje/Aann0Teu/YI5cU/kGw/i+TtX3nqAsi/AttnLjCxdBEjo/sw7XwJzYEH2Lz8L8qjvxO68V0cc6czWluPpddkLJV9WCsHsXLtxVzMAivPJ/pn" +
            "oYxqJKx/B2YL3mDs/GNELngNbcF0JmhShE9E4RpWQmjJIHEVM8hsWEhS0VQUPvk4i9wodU7FxSYJL2kW3q5Z+MrT0cviMapjyYwuoSG/ic6iBqpi0phaXElz" +
            "QjrRKn8MtnL60rNZW5HHruIoDtansTgviQIxC9KVXsxOiuNMUxJn8lTCA9TDx3dsDJIx2c2FZo1gO3006WIGBLl7E2v0I1r4cbxWRa7I7vX+Bjr8AunRBdDp" +
            "E0hfbBKz42NYrLFjl9aRAzotRwwqLlcK7WfK+HW6Kbc6RnCjdBwMqPmrdQz/DNrxwzQ/9sRbsF5nwfZANWsClMzWOzE31IPZkVqmh3oJX9KKujIyz0/JqiAP" +
            "VgcqWW5wZnmwM/sKAhjSjsW86DimRa8xofgo40tfYZzQe5zQ3qzsVeyKXsEh9QWcMw/833U46/aLPhac37qZ8c2bsF7wMm5fPsbuLphefsDY84+QCO2fugTK" +
            "LwQfPH8Fq+pVSCK6sOt6Eb8Dv2J/5E/cjj4kcMMZXMuWCI5vwsyrCyuhv4NiCHtpD7auk7D1aGCsLAtVfCuh03ZiveotRi94Hd+Z+3AvnIKpTwYWmgQsPZNw" +
            "0KUTm9tDacsSUvMHUHim46nJEYyfILJXJQb3fGRmYbhbhhIq+r8wqpz2gk7BX+XUpZUxrbqdHjH/K8Ni0ZlJxZwOoicxhY3l+ewtjuHZ0jiW56dR6qMnyl7G" +
            "YGw0u1J8RO6K48c6DXeaDHxeF8OGADX1Di5Uu/uTL35HriFE1IGObH9PSgzuVOmV1LnLaZEr6PfwZrqfgaXpcazLCGFXhBubFRN5wUM5nN++bfHmVp89j2ZN" +
            "4E6HhN9bn4Yuc/7pHMc/0+WcLHRmTaAl6wNcRK+rme3nxMI4bwYiPGgKcKVOrLYwD7rCPZgWo2d6uIYZojaWxvvQ6W1Ff4A9s2LcsEo7innqy4xPfZGn017g" +
            "qfQDYu3j6dT9TIx/FqeIZ1EmHsAm9RmeKtiJ6azXGTv0gsgBC3Ab3MeELcfw+/4PHK78gumVh4z9GkyE9i6f/o1q/2WkDcsZF9GEcvIeQg9+j82xn3F54w6G" +
            "DW/j1biWkV71mHp2Yuk2efi4PienbhxdJg1fV+5pkfF9cvsxTt2K7bqTTFx5AnnfZoInLUeiiEBi74/EwoenLHyxU8XgF15KTetinOUxjBwtZpuZAWebCBS2" +
            "0Xg7J5Dz5Ho+6e0i29XTkt1IVWIZLbn1dBSKx6m5FBsj0Zu7kKMLYk5OPuuKM3mmKI795cnMSYphXlEpcVJP8hRqni9N5MPyAG6Xq7mRZcNPjUY+Lgtg55Ms" +
            "7+tBsZ0D+aJW8u2UlDooqBNzo1kuo0PtSr9OxfyoADZlBvBmSwBvN+k4XKjhYIo3611seDNUw406X+5PlvLnkAn/PvkfQPcY/u2x4J953nxa7czhWEd2BkhZ" +
            "Z3RlTYQXU/xdmBaroSlMwVBROMs7i0RdJ1Mu9G+J86UhWM3U1BD291WzMC+CJuEDk6NUOMa/ikPya1inv4p5xstYpB3GMu1FTJMOYBH9HNJwsUL3YBm5VbD8" +
            "KrxXncNp5uuY5a/CpU7k7nWv4/7SRyjP38Ty818ZdfFvxpz/H7IL/yB99mO0/Rswia4lePYBAp45j91bP+Dw1o9o1p0gfNoBJF51mKrbsBQ97+DSg5N9B06O" +
            "Ldi71THKIZWouvmEztyBxcLDmMx/EeO8ZwmbvHz4HI++qQ0kl/SRUdZPZEoD/pHF9M7aQn7lEPFpzZTXzKSiYiZNNfNprpxNc8mU4fP7l8aXUZdeTVt+I415" +
            "jRTH5TO7YTJ+Vk4E2asoD4piVnY2O2pL2VMQx+G6POanCS6vqCHFI4BIW2c2l6bxYp6RHxoC+KPNn5sNnlyudeezjlCOlxvYlx0q8lU4C3z1zNJomeMlvNeo" +
            "YkeSlpeKjZyoC+ODVj9uDKq5PUvwe50/b9Sm8k5uClfKUgT/ufL7kIzfe4X2U8UamMAf/VKudOg53xHPLr2UTV5urDJ6MFdkyA6dMx2CJSpC5EypTGbFYA19" +
            "1WmUxfpQHedHTYSO6hBPWkTmrDQoqDa40SG8wCroINbhL2IZcwiLGNHnUfuwi9qPdeRzWITtxSZkN9aBIrulHMAsYj1mudt5KnMl5nlrhGfMIWnJ68TsfBev" +
            "N75AffUfTC78jzGf/A+1mP8uBy8SvPowI+OryN/7HuoNx5Ce/h6HUzeQrTpG7paP/k9/VTOW0hYcpEJ7h3YcHRqxk1Uz0jaJsmnbiZ+5Hdd5B7GfvQ99/zoS" +
            "elcwffcJZm4+Sn7zXJKLu2kYWEnzlFXk1A2R1zCVmp7F4vlOssr6yCvtJye3g8zUOqoLO2it6KQ0vYz8xAIyYgp4ftMLRHgYcTexQ28toyk+jV3dvSzPTRfz" +
            "P5V9tYU8P3kybXFp+NooiVFoSHGy4r057ezL0vNhjZFrPf58PcmNax2OXKy1FHzgwUe1Oj5oDOeDljg+7ozmXIeRz5oVfNVgx712B/4ZcOH3AUtu9Ss4OzlF" +
            "eEEZ3/T38GVzLl82ybk/1YLfBiT8O1P4f68537d58HFLOqcbqlnm6sF6bQBT1Sr6/TT0h/rSlxBCWYCnyJpPzvXvToq/qOVoo+ANwa7B3tSIXFgf6kNrtD+N" +
            "YVrBonJsg1/ELuxFrMKFvkF7sfLfiV3AbuyC9mAdvAvbqGcZ77seS791w/vpzAKXYpO8FpeKnTiWrsIkopm+49/gveNtvM/cxelzGPvRX1h99BtOh86RtfcU" +
            "Y2NKGDxxBcW8nWhPXUV14ivclrxM66EbjBL+b6VuErmvEQe3NhycW7F3FtzvVslTQv/Fe88Q2rwYzdAeIlcfR1Uxhal738Q5LB9tXBXr979Hfd9KQjMbSa7o" +
            "Jr2uj8zmIaKLWkQeHCClbDKpJV0UVPSSX9JJeUnH8PE/cWGpVBTUc/CZE2QLRpdbKIgUud9GMp7Zta3sGZzCvKw09rdUsSQtnhXVdbywZD3Kia4YpBoKjQZ6" +
            "kyP4YucqthQE8u4kI1/1eXCz05Zfu6yH9b3ZKuXrFiWXmpRcaJXxVZcLd3rtedxtBW0T+Fesh932fNXhLhgijgd7NzNVqeCL3hq+7dZzb9CcR0J/5prwQ6cD" +
            "5+p9ebRtMZNlauaoQ9mZWM7ssAQmh4RS6a2lSOtJdVgANXFhlEaIHBoZSHmYkfJQ0f+hBqG/H9UB3tSKWqgQ/F8f4YO1cR9WhuewDtiLnXEPTmK5GHfhYNgh" +
            "nt+CiXETEwM3ifvrsPNZgdx/BYqYtVhmrmZ81hzMEurxbpjF0Cf3UW45TcA3Igs+0f7ib6heOsvga+cxcQ9n44mLTDl5AetFW4h47SLG2YdYdep3zDzrsBBa" +
            "2yrrsZc3Y+FUi7VLDRbOJYw0i6Vz9rM8994twrq2Eda7k8iGOSx+/hSTFu5BYu4j3k82u46cZ92+dwjLaSaxqmf43L9ZLVOG6yC3eTq59VOGj/vMKGglKjqf" +
            "+Jhcls5bx/tvXSAno5GJ4+QEeEWhslMjneDIwq5B9sycy6z8PJYW5rIgL5dHH39GS0Y5GkdvNC460gLD8LGxpiklhUdnT/JMdQRvVKj4pUfDzdLx0OHCPw3W" +
            "PG4Rvd5my51uG34ZcBCv2/FLuy0Pmlx4MMmb94vduDKUCS+tZW1KODkTzbm3ZQ2fi7nyW4eb8H17/hiScqZOwX/7Z7IrP5QOLxWzwvNYkNbC4tJWsj39KA2I" +
            "EPk1hprIePJ8jeQLzigWLFkREECJrw9VxidLR12wnspADcUGJZUhXtgYn8Xabze2+h3Y67eKtfn/ls8GbH3WYy50NzOux8p3FfbaJSJbL0Lmt5AJMQsZkzId" +
            "05Q2JEG5dB2+QN3xrzBbdQDd6S/xfuMcnhtfYPXb14bzuZk2harVB1l49QEZO44T0bacQx8+wkJehKVLKTaKGqzltUx0rMDcpRpr13Im2KchGaMjPn+QQ5/8" +
            "S++6d9HG1rDr6KfkNS4Yvs70GKtgJKPUdE3fzvtXfqOyewnRhW14ROQSntNIUEoNvoL1knJa6exbzrp1Bznz7pfs3fYijpaemE3QIHeNwNbCg6jAFMxH2zCj" +
            "Y5AX1myiNDSC97fv4PZb75GmDUZr447cVotOFYTBw4BBqUNh5kiC2p1f39zPj1sHeb/Bn4tVcm41qvmpQcbDXk9+nCTleqcT3w2o+KrHk0/rlXxUqefThmjY" +
            "t5z/Dqyh3WkUrXIrqhQe3Ni1Q3h8PFfbjXxeK+NDwYI/rG7j8opeahyfYnZ0KLMz6llWv4ANU1ZTkVhMY1YVsZpgEsUqCooVdZBAiX+Q0N5IY3gErTERlBm0" +
            "FPt7iCygJ8dXTkmop9B1B9Y6ka81G8USfe2zRjy3BnvdGpx0a7HzXi1eX4GVt/B97SJc1HOw95iBSeBMxsTOYETcANZFM5AYs9jy+Q/s/ukuHa+9yuDxU0x9" +
            "5T1eETwwXjDYU34NSHyqMFQt4OWL91iwbifH376ErTQdK5cSbIUHWMoqMXEsx9RJ+IFrDbbOBSjU2cPXDzd3yubIyQdsffZDDr56mdjM3uFrTrspcsXP5A5f" +
            "f9gQVsVbZ2+z5dl32H3oA3YdPMOO59/jrfe/581T37B/3wfMHNyIr0csZiOdcbX2Q+ESjdYrE4U0DA9lKCMkpiyZs5Kjzxzm7NGTvLB8I/JRljhIrNA66vFW" +
            "hGHwScDZToO/OpAIuS9GUQM+Y0azqaOSW8d28NW++ZxeWMWJwQyeLddzpD6Ql5uDONIcxhuDBVzeOJVbL+7m3ssH2F2RT/YoCd3O45grOD1XcMPDt09xZecS" +
            "Lm7o5frOQe7uX8y/J1+iVubIZJUrAz4B9MZWsGPmbn79/j9On/yM1JhCsuOKSAtOIzc0lSSRZwr9w8nS6kkX9Znl5S5mlpY0nYKicC0ZIgOWxfhipdmAldda" +
            "bN1XYOe1Ekv9Sqz9V+KsX4VMtwon9yWCrRZi5T4fO+0CnDzmCv2nYa6fwoTQGYyNm8PIqG5sk1qxisimYsEyauYtomvZFobWHaF4yj4kshJcs1fimDBn+JrR" +
            "9qokNu9+kb/+B/ayTGxEr9vIKrCSVmHmUIW5fRVWjjXYOZVgbROPp3uB0CWEseOiKRC19ue/UFGzijHjI3G0y0Dlloe9bSKSkRommBro7NlAbdNCqhvmU1U7" +
            "H6OxGDeXGCwn+DFK4oz1OA0GdSJSywDULkk4CkZ0VyTg4hiIg503C+ZugL8hITgFC4kFruOkRHjFo5MGD9eMRiV40jMGb2UwEW7+xMn1hLu64zbWBE9rM3rL" +
            "c4Wdz+f8vs3cPLqXR2/u4+Hx5/jx4FY+27OOVxbNpjMjDYO5OQm21lQJTYcMgtPUThTKnJiRk85z07o4OL+XXX0NHJzeQ5VWR6tfMI0qX9p84+lIaGL5wHa+" +
            "/Pwxv4v3unLZ8xRlNZESlkuMTwy5QYlk6ENoS8+lLSODFG9PCkQOyA7QkBumI9bblUQ/wf/qZWItxU69BFuvRaL3l4h5sBhHb+HzXkuQyufhKJ2FjXw2Dl5z" +
            "cfaeh73XdFErg8IXBrEJm8V4/y6sQycjjetlnH8pZgHlSJxTBNdXYObdijJxAY7Rswko2fj/rydbhcQmDF1iJ1byUtHrVdi4VGDtVI2tYx02dnXY2tRib1eO" +
            "k+h7B8GBardqLE1TGTFSh8ojG5lbMdaW2aJ/S3GxyRIrA4VrNkq3DOEXCkaN8GDCeF/MTQyYPC04Z0IQ7tJk/D1yUDvG4GoeiM41CYVdIkrHNKR20Xi4J2Np" +
            "qcPHJxF3VSim42TolJGoHQLxcgjFoIhHbmUgQJMu/CICuXOAyANeBMi80Tt4EiHmQqTSiG6iFK+nLAiZYEeyrQtpNo5k2tmTYWtDtOl4QkxGkeBsKvrTnQwf" +
            "L9J9nuwvlpGlU1Gsk1Lq7kC6rRkRT48W2zkSP8GKZkMYWUpf4hURdOQP0JgzhfriWVSUz2Dm3F1s3vwqlYKDwvRJw/seM8JSRM36UxCdQFZIGLE6DZWpcRjd" +
            "7MVtFEt6W0gWs8BGtWB4WYv+tvacK/xgjlizhB/MwF4xA5lsNlLnGThJxVLOEtrPxNZjCDtFr6iJLszkk7DQdmPj18dYrxbBifOxCZqMh/AFB78eFNELcI6Y" +
            "R97QG0zdfg2ZoUvUWT3S0CfHeOdjKqvCWioyn30ldnbVODu0iH5sxsGiEWf7elELOdg7ZGNtloPMqRxPTSlWdgniZwuxsxa1NjYNpbQCN6dCbMyTxDYpqOSZ" +
            "uCszRT/Ho/cqwt0tE7njk/8Fx6FyTsbDNQ25XQyeokalE2NRWKfiKXxI6hxLcEixqPdgbO38UShjcLILQatMRWUbjto6DD9ZAm5WgSido3GXReKjNGBU+RGg" +
            "DCHUPQajc4jQMopYeaDw3SDSFd4kOrqRYOdGpsyDfHcvsj3UZHi4kqKVE6nREhcURox/AKkB/hT6KChQO1KkFH4QEENzQCKF7oFkyUVmF5q2Co3zkpuJNRYR" +
            "E1hMXEQVoYFFFOZMpn/SEjpqp1Bb0EK0fwzJwbGE6/xpKSsTtxqifTQkGnTi7/hSGhNOXpABB8U8bFVzsVDPwsJ9mqiFqSKLTxVZbEh48qDQfQquLtNFrU/H" +
            "xXkIW/kANspewWqTsXfpQKroR6GbjrVXN/bGfsw8mhgnFTk5fAZmiibMfAeZaJxGQMUepm+7jm/kTHT+3Tgqa3D0aMRMWi38v0HoX42dVRUy6xZcTJtxNmsd" +
            "/o63tX0hjmJ+2NtX4ORciWRsMOMtY3GV1mFvXS2yWwNjxyTjLHjB06sWM8EMTs5pyESucHQQnq0pwck+Uej4//q6riY5zutKyZZEcMPE7p6enHtCT047YXOc" +
            "xeaExXJJgABJgCIFCgwSbcmmSlVylewHV/nB6Q+4Sg8u+82/wv/Av+X43NuYBUjJfjjVYXp6puace+6538wCU15P3YQ3kYztI2JsUGMbaMSuULMv+HpbMHxj" +
            "BMIDzPnrKFanMOwJX2eH+jlEbG4JlcgG6vSJJs9lrVWUU+vIMhNW4jUsN6ZIBdqoy3fPdguTBvNiPIF+Jk5fSGM9V8BysojVFPlmdtyudjEptbDcWubssYQN" +
            "dwnjJOf2UhmX7Nn75QnWqaeVwgb223t4uneKD6dH2OX143IPy7URVpubGLuH2OrfYKt7je3uBV68/w21tMf3s4Ilt4/1/gQPD4+x1m5jizgeDXnfErbyDq57" +
            "Q0Rzv4XJOvfnf4VA7i84d3+DaPaXuhav6/Hxr5FO/Rr5xK+RtFnzMdZv5ksksl8hlabnx75ANPWSM9wL8v1TBMl5uvZSOY3XvsZC9SsEu3+N3NbfYXT2z6gP" +
            "6P/2I9TrX7G3fIJw/ENq6RNY9lOYwUdI+j+CPfcU0YVncFJf6P/X/IOfbNOHqBHjGnb6CrH0NULBa+qFOdF4F7HoLQzjkp5wjizniXB4D6ZJb8+cwTJ2EaUn" +
            "pOPHzAh7yGcvUC3f8NwUafsAGd8UVZsZ0j6ijxyhWDhE2Jzwve2S/1XEk7v6PYJDVKgnx1hGNbZBjteRoTfU0wPU0l2kgk00i3soJJdRL69RM1U0ymX2myw6" +
            "+RQGeXJPP16tDDFkPS8RI3cZjdwAy036crqFneoI24Umdpw+vXsTm91THG090p6+2RhhuehinK9g2h1ip8/sUR9irb6Hnc45thpHGJU2sNma4np6i036xlp3" +
            "DaPmEG2ngdUOs2q9gWWnjGmjjuNmDVOnQJ6Y35Nf6/duRuZrxHLfIFH4BvE8a5/HdlL+bY2X7MMvWWNf8PN4qXzHkp/Djv8MkdhnMBM/g5H8VNfwwonn3P9E" +
            "f89hZz/nXPcClvMFcq1fo9z7FkX3F7wH/T32MRKpT5BIP+M9npA/ZsDQLezQe4jSB4zAe+z315ifO+Tr3sIyr7G4cIxo9BrJ5C3ntmPY5kPY4Svigj3jElHz" +
            "nHV+xno/1VwYM494/j579QVi4X1et89z7Bfpc95/C9kYPT/MujWoj8h9ZOx99pGp8p2MMQ/G6BX2BhLmGsqZfUR9zAHJLcQCfRRiayjGV5A2W6ikh/SCEfKx" +
            "JfaXAaq5Mdqs36SZQzVTQbvYRM/posM+0SkwP5QGzJ/MCRn2jdIqmllmi+IylvicfmGASX0Ve+Nj7I6OeG6Fdb2jf3e+zDn/YGkda40B676FYbmDZXcF6/UN" +
            "rDc3sFon3+4YYz5/azDF/uoJ2rzfaps+Qb/Y7o6wUnXw6GAF335yge2aQf6/UpjJL2Fxa7HmBGb856z1z9XjrfgL8uUhxv04z0VjHvdGjDX8CqHoc4XsR5Kf" +
            "0bc5h9f/UrcB6yns1KfMbl9zXvuS2uH1xmPqiufp7VLHRphzoPFQYYSlxq+YB24RDlywjrkfZa3bNwj4TpBhZrQM5v7QOaLhM0XMIP/GqQfzhNxTI4F9mIu7" +
            "yn8kQC8ITcnnAUE9BDnHGXvcpwasfY9/xR69gZ5js8/Q82PhZT6+wn60SZ0sMw9MmReHfHzETDkm733EQ8yNxXVqo0Jd9JCNNHk8pl44L6Q6qKSaika2g66z" +
            "hEFlTEyoixUMqpv0gTGaDnPA6BjrowMM6A1yTY/5YsL+MKEnrLFH9PJ15o0GRrUuOV7DuMrH3KFiVKOv8Jql2hiTFvXQ2WW2OKIemEv4mp18lT2gDjc2hz/8" +
            "y7e43Coyd/+c3L9UyL5wHo6+QNj+VGGSZ4EV/VT5Ft4FdvQzPRcWvqMfI2R/hGDkQ4XsR2LPmaOeI1/6Qtdz/WHOd8x30fgzrX0zwsxnPqZPv+Y+HGKmDz1Q" +
            "7gWigyBz38I7h/SGC5hh5sX5Q92KFgK+I1hB5sMwZ4TQ6SsdnHgg94Jc8gpmYEpfviCXR3e8F5JnMBY37vhPRvaQsqfKf5p5QfgX7kUD1Tx7wnyHHrCHiL/H" +
            "/RbP0R/MAXPREhKRBueTJUUp7cFJ9Zk5W9RHhzNmG+VEB266T56Hd2gyMzo2PaDCPt/ex1JjC3VqppD0tCL8r7bW0OV82Us32APkN2ucN7vs7dU2a7tOD+jf" +
            "YVDpUks99N0BZ5QxOnz+uL1NTaxhvbtNMGuUHGaDPPOgjXfvtxGwP0NI6li4T7zwuBdPl/pmnRvk2SDPJnO58C28CyLUhhV5zvPsy5zZPA2QU85uwnM4Qk/n" +
            "sewbtszy1ASvC4YfYcH3EP4gvd7+QDn36t7TgNS8QPi3zBsYwUv1+UTslv37gfpAIfch5u7Rj5kNrdCJQvj3cOwhfKSwgvsILe5o3QvihodS5lK1oPxzBhT+" +
            "k5Ed5T4V2XqFDRQ5I8q6gdR+whjR2/eokRW4BeaDxQa9psG5pM0+0dStaCEWqqKSHZH/tmqhEOsq3NSQWYH3SC4pGtkJpuML1YGsP8vvRIX7RmkJK/1t1Mi7" +
            "9Ip2tqGZb1gi58wVS+UmOsUauuUWH+8pBuS/T/57zJUdt4t2ZcDZh17QWMP9jXO0in206BuDag2rnAHaeYt+EYffeo6g/YlybsQ/e803jyPUg6XcP79DhNcK" +
            "lHvrmXJsRp9639nEPryr6yDrXSD7IfM9ev0jrXWD21DoXc7ZNwrJcTPfn3l+MHDJLevdeKD8i/+L5/sXj1UD77w95Ww/1fo3g8eK1zrw+I+EDhVxi7Ngljry" +
            "72j/n/EvkLoX7hPW9h1EA0lyn7Q2dU3A8g+RS2wyz+0ibo507rv3wzyePf4tnt7+FfzzDkr5ZW4LuL/L+YXeL5pIWC2Uc8t8TpuZgLNAbo18riFvD5Ex+2gW" +
            "trEzfICTjXfZH3ooxdvsF0PqsoOl1jrWh3tw4hW4yRo6uSZWa0Ot/Y2W9Hfy7bYV3WLnjv8eH2+XmEOpj5bbQasu/lTCf/zhv/D45mN06Qu1fBmDBueVtsu8" +
            "miD/zxCIfKwaCNrPEIqwL7+xFc6NyLM7CO933FuyVvNEs7vUtsBk3Yctj/sA53PTfqzZ7o5vch8Wrw/fcv/h/8t/OOTVfjrxmLM57+E/5Sz6Pt7+8S4O9n/P" +
            "4+M/yX8keHTHf4GzwqD9U82BSfuYNX2g3Fv+LfrBzvf43/QQWVfuk9aa8m4FBrpOHAn2+fwR8wC9PbZMb2lTnw04+VX4FypYn3AGzW/o7wtlPTludFF3qCer" +
            "D2uxRh2Quxaz/NINehXmUHuAIj2inu3rdwqVXAdOjl6T5vyZdtFgZhyS90bWRT6cRCmSRTWeQ9FOM1+66FabzJbMlA4zBb2hxZxfc6pwiTrzYYs6abg9FHO8" +
            "F7lfak+wubLNe+cxavH6ssN+/bH27IA169/eccjksfHE83NTavqpty4X+Ugh+2HzA61vy3rE+dzbejX+rm4t6z3lWLzdMm/V6wP+K+JCORd/F77f7Pmy7+FS" +
            "If1+lv3E84Xzhbl97O38Tj3gNf9Hikj42MMr/gPzW+TzBL531ng85XzAHBjeRY7z4Mz7hfu4ufEa5N3DKh9b1voX7kUDGfKessfUxab+DVk+vY5EfMIZdEIP" +
            "qPHePdhGn5/5Bl+vQ9316D8b9OT7imqeXsIZsZiYwEksKf+tgvhDX/lvMr9Xnd4d/07MQY/cHi7v4m9+8RscrO6glMgxK9IbciWdLUQDzSK5Z08Q/mslF26l" +
            "hiq9Ppd10O+NkUoW0eEsWMhW0W2N0GuyrzBDetx/AL/5RLeS3WQNdtbHhfugeLho4ZUGPO6fai8XP9fZjfDq+kb5l/WaaPSx1reX615nO6l12RcdzPr9jPeZ" +
            "DozwlcK2rsn3AUKBM/V+I3SmGnjn7V3mwPM3av8N7g2v90dC9/n5H2rti/cno4eKwMIaP2vqcG58V/t/intBNDxSDRRS0hsm2gOS9ADT30YmscYMssr3uILx" +
            "8DF6nRsUMpwpOCOE/cx92U2UcsyPxS2Us+uaFVN2jzoaUAPkIsEZMCsaqPO4wWvknlU+r4k6571yjj2+Irmxwl7dxz/+/h+w1pN5I41uvalcd8oe926BnDs1" +
            "1MvUQLWOSqUCx3FQrrgo8Vy5VEeJ/lCnD7iVHirsGSX2Dh/nbR/n7kXmMenfkts0m2lGf+JxLHmOtW7ZXn7zB9+j138EX+DdO95n+V34nnn4mwgHrrSXW+EH" +
            "CjN0pX1drvX7zhWeDuT6c4XwL5wLZK3ft3DA+e+aWfBGPUDORaxz6oiZ3jjhTH/A+3K2i3ImtE44O8h6331iqrVvs94FUfMVLGYCc5PzRQ+p+K4i7GNeS0+Z" +
            "F5buNBBm5rdDQ/K3xvoXTxgrYtaEWlujj22wB5yiWbvmXLqr68hV50C/U8rL3xokqJloBynmw2yiiwx7vSAfbyFrcl6MuUiEC5r7UrEy7+Hyfg1UmdeUW/Lv" +
            "xPMoRLPcZlHJFKmpitZ5KVNmv29pz68UXtd+qVRS/muNOlLpLPKFMnXAvkK/qLGnlOgxjfoKa/sR8R4WAvRi9u1F/w0WA9e6FR3INkCNyL5kONmX3i5rOKIP" +
            "r/a/O7+9yf/sOOQ/V75FA8K9QLMdH59dO6t76f0C2ZfcJ7Uvs77UvdT/j/6Mc1mSvYV9Phggv5z1pe4T9gU/a762bw+BxW1kkpfKvfJPz/f431XM+E9Ed/Hk" +
            "0d9i7m16tbVBfpnrmfli5ipSUeb/jGhnrGsA+eSWekFooeX1g+SOft+0MDfBoPtU71VxzlB2jlEp0uudPb6fCc/36AkN6qvF57SR42yXT7fIXYu17GJjuI/t" +
            "lSP6UpzcMaNxpi8XW7y+wDmiBpeeXWPur+XY1/Pkl9yX6f+lovBdZ1/poFFpMntQJwXqKe8gny2Q66KiWnO1F5Qr9AVqJc3e0qytoF5b1YwWNG4w7+NnHX6o" +
            "3FtRzmh+1mn0A81ykuFkK8fS42XNxnFeIpf71OOekLVZxYzvN+Y4PUf+ZZaXrfA+w+x6qXvJ+wLt/eELXdOVWT8cPNdZL2JeMv/dau1fnP89+9pLzM9tw46c" +
            "qQeYst5jekjF6Sf+XdbnvgfyLZjxrzC34ZsfYmX8HD/58xZikU2+p2Vet8I63SH/m6qFpL2uCC32VQdt9wyt6inMwJDewVm7+xH+89//Rz0gnzng53+fWpzo" +
            "b0vSsSH3ORdGOScmmuS+TtQ045Xo81L3J7vXONrl5+BL4ub6I4xGO9R6DO2GrCVwliTvDc76ku8apYb6fNlx7/gX7mVbZf93nbqer5TY59sdXleC69Irqi75" +
            "KqBe5TySq6NWFo0N4AuyD9OPF/yXyr8cx1MfUgdXiCbo/8atci86kTkuEHioGT6Z/Fjn9z/F/wwz/mf1P+NffOAOd33/6o5/2Rf+Q6FzXe8T/mXWk7wnnv/W" +
            "W8vodj4n5wfKfyp5rT4Qsdgj5reZxWX99gzz99b+iH/5DuhNJKJT5T6d2Ecufcj3NNHvDVP2tv4uIJfc1TwQCU2YISbIUBOVHL2dPUL4z6Xvo5Bn/givI25v" +
            "kbcB/apPzkfM3FNk2efT8Y7WfoY9Pst5Tnq8oEAd1PI93HsrgLRdZu0v0eey/Dwz5K2LbIo1nq2rB9SlH7D2y/mq1rgjHJeF67L2AvF+4b7OrCjbZrWF0dIY" +
            "9QrzRCGHRrWCbDqj2tGZgPprVEcIkXuD/TXAWpT9RXJkcOaamz+Gn/wEZT4jv5LjZetnfhdI3hctzDKfQOc53kPw/T4g/Xw206m/UweBxdO7c7O8p9y/qv9g" +
            "8Iz5/z4/zxPlXZBOySxxpn/baxo8x4zvW9xj/qZH2GfUxgCfv/g3/Os//Tf3u3f8y/dAHnYUM/7lu0HxAckBwqF4uHiAGVpRHYQXRzoPlsi5aCEw3+Ms0VF/" +
            "aNcukcvQU+Tfm0ht8b336fNjZkBmf4M+nx6z1pfItdfzswlqIMF+HKsocnEXsWCOs9gWCsk6PbvJ2mwxu3EedIf0Elf5lxwoGhAvKDILFLJl8u++4t/RGhf+" +
            "JS+4nAVkdihmSmi58lsVh72oQN1kqAVeSw+Qa8u8d7cu/POz5nwVDF3o1kde5hdOON8zYwk/5FTmONO81e/tZjxHo09eecH3QO6Dodf+Lxnfw9l3+BcvEP5F" +
            "F7NZb5b7ZvxL/Qvn8ehDfsZX2v+DgUPFj3+0ztmGeWVhFxn5fph5TzD3zia94TOsTr5kLe/d9f//i//5e0usM/bexTHf04Tcr9I79qjPZeVfZgIrID1/yHl/" +
            "rDqYDB6jU3+g9R+Pjfm6ffh89Pr0RHnPZybMEuKzY818UvvpWMvjP95kXxENcKZPNDgH8jhSgbGYxPbmKU5P3mcWTqgPiAdIn3Ay9PuMpwNBgT5QYP0Xil7G" +
            "r5a9+pfHqoUWr29wtijRL6rKf61UQDpisE8UUS8UmWPS6Lu8d6JADk8Vs/U22cr36bX659TAtWpAfEBqXvj1+S6xsHCuXiBbf+haIT7xHYSuFML9LN/L/swH" +
            "ZCuaeJN/ORZoLzC9HuBfPFQNCPeS/32L+1r/8dgD7E9/h4V5zluxS95/T/lfWNhEPM77BHb5PvZYxwR5FnyHf9a8QPxfcmA+c0J/prZ87P2pA2pnRTWQsncR" +
            "M9ZZUwfs+9f8jI/12A5xDrB5bXbCesni+PApM0pd/644YrqcR+m3yZ72fUEq2vQ8IEotMAtm7Kbm/3ysSe+v6rpvMdfVXG5GCuh1N9j7ipoVpJ6d1GsvKBaY" +
            "H6iBbM5BocB6Ljua/cQb3HwHtUIXLjVQy8lvVUv0DWprMkCrkmcfyaKUSuvaYTaSVu79/pO7eUu4+eEP5Hcwv0I0dqt9Qfr6rA+IDkQD4gmylbygGSLwPfB5" +
            "AvF+4d63eKaYcT7L+LOcL1u/7/SOf/l9j2hAMp/wLz1A5n3J/H4f53r7EpPxL3l8gHfubSr/Jeex8m9Z7Ak+8hs5YD3Tz0Pbb2jgj/kXziPG1qt5YMpMN1QP" +
            "kJ4QN7dYLwfKv3Duu9fTreSDSHjI12giFq/j6PARe1KTWqzx/ReZrca8n8v7NBXy3UCGOSBjt5A0Wf9WQ7WQCJd1zTcTdflec6ztNqLxMpKSEch9QWpZ8iLP" +
            "5RJl5TjPPJgj3uTfyZY4uxTIdUM1UEk1UErSG9LUkB3B73/zK0w69I2YzfMJNPIljBo9/C8sp85M";
        /// <summary>
        /// Creates the album art canvas and attaches it to the launch panel.
        /// Call once from LaunchPanelUISetup(); guarded against re-creation.
        /// </summary>
        internal static void CreateAlbumArtCanvas(Transform parent, int layer)
        {
            if (canvasObject != null) return;

            defaultTexture = LoadDefaultTexture();

            // ── Canvas root ──────────────────────────────────────────────
            canvasObject = new GameObject("AlbumArtCanvas");
            canvasObject.layer = layer;
            canvasObject.transform.SetParent(parent, false);
            canvasObject.transform.localPosition = ArtPosition;
            canvasObject.transform.localScale = ArtScale;
            canvasObject.transform.localRotation = Quaternion.identity;

            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            RectTransform canvasRt = canvasObject.GetComponent<RectTransform>();
            canvasRt.sizeDelta = ArtRectSize;

            // ── RawImage child ───────────────────────────────────────────
            GameObject imageObject = new GameObject("AlbumArtImage");
            imageObject.layer = layer;
            imageObject.transform.SetParent(canvasObject.transform, false);

            rawImage = imageObject.AddComponent<RawImage>();
            rawImage.color = Color.white;

            RectTransform imageRt = imageObject.GetComponent<RectTransform>();
            imageRt.anchorMin = Vector2.zero;
            imageRt.anchorMax = Vector2.one;
            imageRt.offsetMin = Vector2.zero;
            imageRt.offsetMax = Vector2.zero;
            imageRt.localPosition = Vector3.zero;
            imageRt.localRotation = Quaternion.identity;
            imageRt.localScale = Vector3.one;

            // Show default art immediately
            rawImage.texture = defaultTexture;
            canvasObject.SetActive(defaultTexture != null);

            MelonLogger.Log("[AlbumArt] Canvas created");
        }

        /// <summary>
        /// Loads album art for the given song ID from SongDataLoader and
        /// displays it. Falls back to the default Audica art if none is available.
        /// Call from UpdateLaunchPanelInfo() every time the selected song changes.
        /// </summary>
        internal static void UpdateAlbumArt(string songID)
        {
            if (canvasObject == null || rawImage == null)
            {
                MelonLogger.Log("[AlbumArt] UpdateAlbumArt called but canvas not ready");
                return;
            }

            // Clean up the previous song texture (but not the default).
            DestroyCurrentTexture();

            byte[] artData = GetAlbumArtData(songID);

            if (artData != null && artData.Length > 0)
            {
                Texture2D tex = new Texture2D(2, 2);
                if (UnityEngine.ImageConversion.LoadImage(tex, artData))
                {
                    currentTexture = tex;
                    rawImage.texture = currentTexture;
                    canvasObject.SetActive(true);
                    MelonLogger.Log($"[AlbumArt] Displaying album art for '{songID}'");
                    return;
                }

                MelonLogger.Log($"[AlbumArt] Failed to decode image data for '{songID}', using default");
                GameObject.Destroy(tex);
            }

            // No art or decode failed — show default
            rawImage.texture = defaultTexture;
            canvasObject.SetActive(defaultTexture != null);
        }

        /// <summary>
        /// Destroys the canvas and cleans up all resources.
        /// Safe to call even if nothing has been created yet.
        /// </summary>
        internal static void DestroyAlbumArtCanvas()
        {
            DestroyCurrentTexture();

            if (defaultTexture != null)
            {
                GameObject.Destroy(defaultTexture);
                defaultTexture = null;
            }

            if (canvasObject != null)
            {
                GameObject.Destroy(canvasObject);
                canvasObject = null;
                rawImage = null;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static byte[] GetAlbumArtData(string songID)
        {
            try
            {
                var allData = AudicaModding.SongDataLoader.AllSongData;

                if (allData == null || !allData.ContainsKey(songID))
                    return null;

                var songData = allData[songID];
                if (songData == null)
                    return null;

                return songData.albumArtData;
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[AlbumArt] Error reading SongDataLoader data: {e.Message}");
                return null;
            }
        }

        private static Texture2D LoadDefaultTexture()
        {
            try
            {
                byte[] compressed = Convert.FromBase64String(DefaultArtBase64);
                byte[] rawRGBA = DecompressZlib(compressed);

                int size = 128;
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.name = "AlbumArtDefault";
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

                Il2CppStructArray<Color> pixels = new Il2CppStructArray<Color>(size * size);
                for (int i = 0; i < size * size; i++)
                {
                    int idx = i * 4;
                    pixels[i] = new Color(
                        rawRGBA[idx] / 255f,
                        rawRGBA[idx + 1] / 255f,
                        rawRGBA[idx + 2] / 255f,
                        rawRGBA[idx + 3] / 255f
                    );
                }

                tex.SetPixels(pixels);
                tex.Apply();
                return tex;
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[AlbumArt] Failed to load default texture: {e.Message}");
                return null;
            }
        }

        private static byte[] DecompressZlib(byte[] compressed)
        {
            // Skip the 2-byte zlib header to get the raw deflate stream
            using (var input = new MemoryStream(compressed, 2, compressed.Length - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        private static void DestroyCurrentTexture()
        {
            if (currentTexture != null)
            {
                GameObject.Destroy(currentTexture);
                currentTexture = null;
            }

            if (rawImage != null && rawImage.texture != defaultTexture)
                rawImage.texture = null;
        }
    }
}